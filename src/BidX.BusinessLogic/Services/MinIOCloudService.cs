using Amazon.S3;
using Amazon.S3.Model;
using BidX.BusinessLogic.DTOs.CloudDTOs;
using BidX.BusinessLogic.DTOs.CommonDTOs;
using BidX.BusinessLogic.Interfaces;
using FileTypeChecker.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using System.Net;
using System.Xml.Linq; 
namespace BidX.BusinessLogic.Services;

public class MinIOCloudService : ICloudService
{
    private const string ThumbnailCropMode = "fill";
    private const string ProductImageCropMode = "fit";
    private readonly (int Width, int Height) thumbnailSize;
    private readonly (int Width, int Height) productImageSize;
    private readonly int maxIconSizeAllowed;
    private readonly int maxImageSizeAllowed;
    private readonly IAmazonS3 s3Client;
    private readonly string bucketName;
    private readonly string publicEndpoint;
    private readonly ILogger<MinIOCloudService> logger;

    public MinIOCloudService(ILogger<MinIOCloudService> logger, IConfiguration configuration)
    {
        this.logger = logger;

        // MinIO connection setup
        var config = new AmazonS3Config
        {
            ServiceURL = configuration["MinIO:Endpoint"],   // e.g. http://localhost:9000
            ForcePathStyle = true,                          // Required for MinIO
        };

        s3Client = new AmazonS3Client(
            awsAccessKeyId: configuration["MinIO:AccessKey"],
            awsSecretAccessKey: configuration["MinIO:SecretKey"],
            clientConfig: config
        );

        bucketName = configuration["MinIO:BucketName"] ?? "bidx-uploads";

        // Public URL base for generating file URLs (e.g. http://localhost:9000/bidx-uploads)
        publicEndpoint = $"{configuration["MinIO:Endpoint"]}/{bucketName}";

        if (!int.TryParse(configuration["images:MaxIconSizeAllowed"], out maxIconSizeAllowed))
            maxIconSizeAllowed = 256 * 1024; // 256 KB

        if (!int.TryParse(configuration["images:MaxImageSizeAllowed"], out maxImageSizeAllowed))
            maxImageSizeAllowed = 5 * 1024 * 1024; // 1 MB

        if (!int.TryParse(configuration["images:ThumbnailWidth"], out thumbnailSize.Width) ||
            !int.TryParse(configuration["images:ThumbnailHeight"], out thumbnailSize.Height))
        {
            thumbnailSize.Width = 200;
            thumbnailSize.Height = 200;
        }

        if (!int.TryParse(configuration["images:ProductImageWidth"], out productImageSize.Width) ||
            !int.TryParse(configuration["images:ProductImageHeight"], out productImageSize.Height))
        {
            // Стало — больше размер, сохраняет пропорции
            productImageSize.Width = 1920;
            productImageSize.Height = 1080;
        }
    }

    // ──────────────────────────────────────────────
    //  Public API  (same interface as Cloudinary)
    // ──────────────────────────────────────────────

    public async Task<Result<UploadResponse>> UploadSvgIcon(Stream icon)
    {
        var validationResult = ValidateIcon(icon);
        if (!validationResult.Succeeded)
            return Result<UploadResponse>.Failure(validationResult.Error!);

        var response = await UploadIcon(icon);
        return Result<UploadResponse>.Success(response);
    }

    public async Task<Result<UploadResponse>> UploadThumbnail(Stream image)
    {
        var validationResult = ValidateImage(image);
        if (!validationResult.Succeeded)
            return Result<UploadResponse>.Failure(validationResult.Error!);

        var response = await UploadImage(image, thumbnailSize, ThumbnailCropMode);
        return Result<UploadResponse>.Success(response);
    }

    public async Task<Result<UploadResponse[]>> UploadImages(IEnumerable<Stream> images)
    {
        foreach (var image in images)
        {
            var validationResult = ValidateImage(image);
            if (!validationResult.Succeeded)
                return Result<UploadResponse[]>.Failure(validationResult.Error!);
        }

        var uploadTasks = images.Select(image => UploadImage(image, productImageSize, ProductImageCropMode));
        var response = await Task.WhenAll(uploadTasks);
        return Result<UploadResponse[]>.Success(response);
    }

    // ──────────────────────────────────────────────
    //  Upload helpers
    // ──────────────────────────────────────────────

    private async Task<UploadResponse> UploadIcon(Stream icon)
    {
        var fileId = Guid.NewGuid();
        var objectKey = $"icons/{fileId}.svg";

        icon.Position = 0;
        await UploadToMinIO(icon, objectKey, "image/svg+xml");

        return new UploadResponse
        {
            FileId = fileId,
            FileUrl = $"{publicEndpoint}/{objectKey}"
        };
    }

    private async Task<UploadResponse> UploadImage(Stream image, (int Width, int Height) size, string cropMode)
    {
        var fileId = Guid.NewGuid();
        var objectKey = $"images/{fileId}.jpg";

        // Resize / crop with ImageSharp (replaces Cloudinary transformations)
        await using var processedStream = await ResizeImage(image, size, cropMode);

        await UploadToMinIO(processedStream, objectKey, "image/jpeg");

        return new UploadResponse
        {
            FileId = fileId,
            FileUrl = $"{publicEndpoint}/{objectKey}"
        };
    }

    private async Task UploadToMinIO(Stream stream, string objectKey, string contentType)
    {
        stream.Position = 0;

        var request = new PutObjectRequest
        {
            BucketName = bucketName,
            Key = objectKey,
            InputStream = stream,
            ContentType = contentType,
            CannedACL = S3CannedACL.PublicRead   // Makes the file publicly accessible
        };

        var result = await s3Client.PutObjectAsync(request);

        if (result.HttpStatusCode != HttpStatusCode.OK)
            throw new Exception($"MinIO upload failed for '{objectKey}'. Status: {result.HttpStatusCode}");

        logger.LogInformation("Uploaded '{Key}' to MinIO bucket '{Bucket}'", objectKey, bucketName);
    }

    // ──────────────────────────────────────────────
    //  Image processing (replaces Cloudinary transforms)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Resizes the image using the same crop semantics as Cloudinary:
    ///   "fill" → crop to exact dimensions (ResizeMode.Crop)
    ///   "fit"  → fit inside dimensions, keep aspect ratio (ResizeMode.Max)
    /// </summary>
    private static async Task<MemoryStream> ResizeImage(
        Stream source,
        (int Width, int Height) size,
        string cropMode)
    {
        source.Position = 0;
        using var img = await SixLabors.ImageSharp.Image.LoadAsync(source);

        // Если изображение меньше целевого — НЕ увеличивать
        if (img.Width <= size.Width && img.Height <= size.Height)
        {
            var output = new MemoryStream();
            await img.SaveAsJpegAsync(output, new JpegEncoder { Quality = 95 });
            output.Position = 0;
            return output;
        }

        var resizeMode = cropMode == ThumbnailCropMode
            ? ResizeMode.Crop
            : ResizeMode.Max; // сохраняет пропорции

        img.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(size.Width, size.Height),
            Mode = resizeMode
        }));

        var outputResized = new MemoryStream();
        await img.SaveAsJpegAsync(outputResized, new JpegEncoder { Quality = 95 });
        outputResized.Position = 0;
        return outputResized;
    }

    // ──────────────────────────────────────────────
    //  Validation  (identical logic to Cloudinary service)
    // ──────────────────────────────────────────────

    private Result ValidateIcon(Stream icon)
    {
        if (icon.Length > maxIconSizeAllowed || icon.Length <= 0)
            return Result.Failure(ErrorCode.UPLOADED_FILE_INVALID,
                [$"The icon size must not exceed {maxIconSizeAllowed / 1024} KB."]);

        if (!IsSvgFile(icon))
            return Result.Failure(ErrorCode.UPLOADED_FILE_INVALID,
                ["The only icon format supported is SVG."]);

        return Result.Success();
    }

    private Result ValidateImage(Stream image)
    {
        if (image.Length > maxImageSizeAllowed || image.Length <= 0)
            return Result.Failure(ErrorCode.UPLOADED_FILE_INVALID,
                [$"There is an image exceeds the maximum size limit of {maxImageSizeAllowed / 1024} KB."]);

        if (!IsImageFile(image))
            return Result.Failure(ErrorCode.UPLOADED_FILE_INVALID,
                ["There is an image in an invalid format."]);

        return Result.Success();
    }

    private static bool IsSvgFile(Stream stream)
    {
        try
        {
            stream.Position = 0;
            var doc = XDocument.Load(stream);
            stream.Position = 0;
            return doc.Root?.Name.LocalName == "svg";
        }
        catch
        {
            stream.Position = 0;
            return false;
        }
    }

    private static bool IsImageFile(Stream stream)
    {
        stream.Position = 0;
        var isImage = stream.IsImage();
        stream.Position = 0;
        return isImage;
    }
}