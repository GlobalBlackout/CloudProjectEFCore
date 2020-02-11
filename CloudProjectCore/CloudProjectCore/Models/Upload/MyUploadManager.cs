﻿using Microsoft.AspNetCore.Http;
using System.IO;
using System.Threading.Tasks;
using ToolManager;
using System.Drawing;
using System.Drawing.Imaging;
using ToolManager.ComputerVision;
using System.Collections.Generic;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using ToolManager.BlobStorage;
using CloudProjectCore.Models.Photo;
using System.Linq;
using System;
using System.Text;
using ToolManager.MongoDB;

namespace CloudProjectCore.Models.Upload
{
    public static class MyUploadManager
    {
        public static async Task<Responses> UploadNewPhoto(IFormFile file, string userId)
        {
            Stream fileStream = new MemoryStream();
            file.CopyTo(fileStream);

            Image image = null;

            Stream photoForBlobStorageOriginalSize = new MemoryStream();
            Stream photoForBlobStoragePreview = new MemoryStream();
            Stream photoForComputerVision = new MemoryStream();

            using (Stream photo = fileStream)
            {
                CopyStream(photo, photoForBlobStorageOriginalSize);
                CopyStream(photo, photoForComputerVision);

                image = Image.FromStream(photo);
            }

            MakePreview(image, photoForBlobStoragePreview);

            var exifDataResponse = GetExifDataFromImage(image);
            
            var imageTags = GetTagsAsync(photoForComputerVision);

            var originalImageUploadData = UploadPhotoToBlobStorageAsync
                (photoForBlobStorageOriginalSize, GetNewNameForStorage(Path.GetExtension(file.FileName)), userId);

            var previewImageUploadData = UploadPhotoToBlobStorageAsync
                (photoForBlobStoragePreview, GetNewNameForStorage(".png"), userId);

            await Task.WhenAll(imageTags, originalImageUploadData, previewImageUploadData);

            PhotoModel photoModel = new PhotoModel
            {
                _id = new MongoDB.Bson.ObjectId(),
                UserId = userId,
                ImageName = file.FileName,
                Tags = imageTags.Result,
                PhotoTimeOfUpload = DateTime.UtcNow.ToString(@"YYYY:MM:dd hh:mm:ss"),
                PhotoPhatOriginalSize = originalImageUploadData.Result.PhotoPhat,
                PhotoPhatPreview = previewImageUploadData.Result.PhotoPhat,
                photoGpsLatitude = exifDataResponse.photoGpsLatitude,
                photoGpsLongitude = exifDataResponse.photoGpsLongitude,
                photoTagImageWidth = exifDataResponse.photoTagImageWidth,
                photoTagImageHeight = exifDataResponse.photoTagImageHeight,
                photoTagDateTime = exifDataResponse.photoTagDateTime,
                photoTagThumbnailEquipModel = exifDataResponse.photoTagThumbnailEquipModel
            };

            return await UploadNewPhotoOnMongoDBAsync(photoModel);
        }

        private static void CopyStream(Stream stream, Stream streamDestination)
        {
            if (stream.Position != 0)
                stream.Seek(0, SeekOrigin.Begin);

            stream.CopyTo(streamDestination);
            streamDestination.Seek(0, SeekOrigin.Begin);
            stream.Seek(0, SeekOrigin.Begin);
        }
        private static async Task<List<string>> GetTagsAsync(Stream photoToBeElaborate)
        {
            var featureList = new List<VisualFeatureTypes>()
            {
                VisualFeatureTypes.Description,
                VisualFeatureTypes.Tags
            };

            ComputerVisionManager computerVision = 
                new ComputerVisionManager(Variables.ComputerVisionEndpoint, Variables.ComputerVisionKey, featureList);

            return await computerVision.GetTagsFromImageAsync(photoToBeElaborate);
        }
        private static async Task<PhotoResponseForBlobStorage> UploadPhotoToBlobStorageAsync
            (Stream photo, string photoName, string userId)
        {
            PhotoResponseForBlobStorage photoResponse = new PhotoResponseForBlobStorage();

            BlobStorageManager blobStorageManager = new BlobStorageManager(Variables.BlobStorageConnectionString, userId);
            var response = await blobStorageManager.AddDocumentAsync(photo, photoName);

            if (response.IsSuccess)            
                photoResponse.PhotoPhat = blobStorageManager.GetDocumentPath(photoName);

            return photoResponse;
        }
        private static PhotoResponseForExif GetExifDataFromImage(Image photo)
        {
            PhotoResponseForExif responseForExif = new PhotoResponseForExif();

            PropertyItem[] exifArray = photo.PropertyItems;

            if (exifArray.Length == 0)
                return responseForExif;

            Dictionary<int, byte[]> exifDictionary = 
                exifArray.ToDictionary(x => x.Id, x => x.Value != null ? x.Value : new byte[] { });

            responseForExif.photoTagImageWidth = photo.Width.ToString();
            responseForExif.photoTagImageHeight = photo.Height.ToString();

            responseForExif.photoGpsLatitude = exifDictionary.ContainsKey(0x0002) 
                ? (double?)GetGPSValues(exifDictionary[0x0002]) 
                : null;

            responseForExif.photoGpsLongitude = exifDictionary.ContainsKey(0x0004) 
                ? (double?)GetGPSValues(exifDictionary[0x0004]) 
                : null;

            responseForExif.photoTagDateTime = exifDictionary.ContainsKey(0x0132) 
                ? Encoding.UTF8.GetString(exifDictionary[0x0132]).Replace("\0", "") 
                : "";
            
            responseForExif.photoTagThumbnailEquipModel = 
                exifDictionary.ContainsKey(0x010F) && exifDictionary.ContainsKey(0x0110) 
                ? Encoding.UTF8.GetString(exifDictionary[0x010F]).Replace("\0", "")
                + "/" 
                + Encoding.UTF8.GetString(exifDictionary[0x0110]).Replace("\0", "") 
                : "";

            return responseForExif;
        }
        private static double GetGPSValues(byte[] value)
        {
            byte[] degrees1 = new byte[] { value[0], value[1], value[2], value[3] };
            byte[] degrees2 = new byte[] { value[4], value[5], value[6], value[7] };

            byte[] first1 = new byte[] { value[8], value[9], value[10], value[11] };
            byte[] first2 = new byte[] { value[12], value[13], value[14], value[15] };

            byte[] second1 = new byte[] { value[16], value[17], value[18], value[19] };
            byte[] second2 = new byte[] { value[20], value[21], value[22], value[23] };

            double degrees = (double)BitConverter.ToInt32(degrees1, 0) / BitConverter.ToInt32(degrees2, 0);
            double firsts = (double)BitConverter.ToInt32(first1, 0) / BitConverter.ToInt32(first2, 0);
            double seconds = (double)BitConverter.ToInt32(second1, 0) / BitConverter.ToInt32(second2, 0);

            return Math.Round(degrees + (firsts / 60) + (seconds / 3600), 5);
        }
        private static async Task<Responses> UploadNewPhotoOnMongoDBAsync(PhotoModel photo)
        {
            MongoDBManager mongoDBManager = 
                new MongoDBManager(Variables.MongoDBConnectionStringRW, Variables.MongoDBDatbaseName);

            CollectionManager<PhotoModel> collectionManager = 
                new CollectionManager<PhotoModel>(mongoDBManager.database, Variables.MongoDBPhotosCollectionName);

            return await collectionManager.AddDocumentAsync(photo);
        }
        private static string GetNewNameForStorage(string fileExtention)
        {
            return Guid.NewGuid().ToString() + fileExtention;
        }

        #region Image modifier
        private static void MakePreview(Image image, Stream streamDestination)
        {
            EncoderParameters ep = new EncoderParameters();
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 0L);

            ImageCodecInfo imageEncoder = GetEncoder(ImageFormat.Png);

            var photoPreview = ResizeImage(image);

            photoPreview.Save(streamDestination, imageEncoder, ep);
            streamDestination.Seek(0, SeekOrigin.Begin);
        }
        private static Bitmap ResizeImage(Image image)
        {
            return new Bitmap(image, new Size(300, 169));
        }
        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();
            foreach (ImageCodecInfo codec in codecs)            
                if (codec.FormatID == format.Guid)                
                    return codec;               
            
            return null;
        }
        #endregion
    }
}