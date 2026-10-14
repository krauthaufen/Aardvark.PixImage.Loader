using System.Collections.Generic;

namespace PixCodec
{
    /// <summary>Tag names of the TIFF 6.0, TIFF/EP, DNG and EXIF 3.0 (CIPA DC-008) specifications.</summary>
    public static class ExifTagNames
    {
        /// <summary>The name of a tag in a directory; "Tag0x1234" when unknown.</summary>
        public static string Name(ExifIfd ifd, ushort tag)
        {
            var table = ifd switch { ExifIfd.Gps => Gps, ExifIfd.Interop => Interop, _ => Main };
            return table.TryGetValue(tag, out var n) ? n : (ifd != ExifIfd.Gps && ifd != ExifIfd.Interop && Main.TryGetValue(tag, out n) ? n : $"Tag0x{tag:X4}");
        }

        /// <summary>The tag number of a name in a directory (case insensitive), or -1.</summary>
        public static int Tag(ExifIfd ifd, string name)
        {
            var table = ifd switch { ExifIfd.Gps => Gps, ExifIfd.Interop => Interop, _ => Main };
            foreach (var kv in table) if (string.Equals(kv.Value, name, System.StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return -1;
        }

        // IFD0 / IFD1 / EXIF directory (their tag numbers do not overlap)
        static readonly Dictionary<ushort, string> Main = new()
        {
            [0x000B] = "ProcessingSoftware", [0x00FE] = "NewSubfileType", [0x00FF] = "SubfileType",
            [0x0100] = "ImageWidth", [0x0101] = "ImageLength", [0x0102] = "BitsPerSample", [0x0103] = "Compression",
            [0x0106] = "PhotometricInterpretation", [0x0107] = "Thresholding", [0x0108] = "CellWidth", [0x0109] = "CellLength",
            [0x010A] = "FillOrder", [0x010D] = "DocumentName", [0x010E] = "ImageDescription", [0x010F] = "Make", [0x0110] = "Model",
            [0x0111] = "StripOffsets", [0x0112] = "Orientation", [0x0115] = "SamplesPerPixel", [0x0116] = "RowsPerStrip",
            [0x0117] = "StripByteCounts", [0x0118] = "MinSampleValue", [0x0119] = "MaxSampleValue", [0x011A] = "XResolution",
            [0x011B] = "YResolution", [0x011C] = "PlanarConfiguration", [0x011D] = "PageName", [0x011E] = "XPosition",
            [0x011F] = "YPosition", [0x0122] = "GrayResponseUnit", [0x0123] = "GrayResponseCurve", [0x0124] = "T4Options",
            [0x0125] = "T6Options", [0x0128] = "ResolutionUnit", [0x0129] = "PageNumber", [0x012D] = "TransferFunction",
            [0x0131] = "Software", [0x0132] = "DateTime", [0x013B] = "Artist", [0x013C] = "HostComputer", [0x013D] = "Predictor",
            [0x013E] = "WhitePoint", [0x013F] = "PrimaryChromaticities", [0x0140] = "ColorMap", [0x0141] = "HalftoneHints",
            [0x0142] = "TileWidth", [0x0143] = "TileLength", [0x0144] = "TileOffsets", [0x0145] = "TileByteCounts",
            [0x014A] = "SubIFDs", [0x014C] = "InkSet", [0x014D] = "InkNames", [0x014E] = "NumberOfInks", [0x0150] = "DotRange",
            [0x0151] = "TargetPrinter", [0x0152] = "ExtraSamples", [0x0153] = "SampleFormat", [0x0154] = "SMinSampleValue",
            [0x0155] = "SMaxSampleValue", [0x0156] = "TransferRange", [0x0157] = "ClipPath", [0x015A] = "Indexed",
            [0x015B] = "JPEGTables", [0x015F] = "OPIProxy", [0x0200] = "JPEGProc", [0x0201] = "JPEGInterchangeFormat",
            [0x0202] = "JPEGInterchangeFormatLength", [0x0211] = "YCbCrCoefficients", [0x0212] = "YCbCrSubSampling",
            [0x0213] = "YCbCrPositioning", [0x0214] = "ReferenceBlackWhite", [0x02BC] = "XMLPacket", [0x4746] = "Rating",
            [0x4749] = "RatingPercent", [0x800D] = "ImageID", [0x828D] = "CFARepeatPatternDim", [0x828E] = "CFAPattern2",
            [0x828F] = "BatteryLevel", [0x8298] = "Copyright", [0x829A] = "ExposureTime", [0x829D] = "FNumber",
            [0x83BB] = "IPTCNAA", [0x8649] = "ImageResources", [0x8769] = "ExifIFDPointer", [0x8773] = "InterColorProfile",
            [0x8822] = "ExposureProgram", [0x8824] = "SpectralSensitivity", [0x8825] = "GPSInfoIFDPointer",
            [0x8827] = "PhotographicSensitivity", [0x8828] = "OECF", [0x8829] = "Interlace", [0x882A] = "TimeZoneOffset",
            [0x882B] = "SelfTimerMode", [0x8830] = "SensitivityType", [0x8831] = "StandardOutputSensitivity",
            [0x8832] = "RecommendedExposureIndex", [0x8833] = "ISOSpeed", [0x8834] = "ISOSpeedLatitudeyyy",
            [0x8835] = "ISOSpeedLatitudezzz", [0x9000] = "ExifVersion", [0x9003] = "DateTimeOriginal",
            [0x9004] = "DateTimeDigitized", [0x9010] = "OffsetTime", [0x9011] = "OffsetTimeOriginal",
            [0x9012] = "OffsetTimeDigitized", [0x9101] = "ComponentsConfiguration", [0x9102] = "CompressedBitsPerPixel",
            [0x9201] = "ShutterSpeedValue", [0x9202] = "ApertureValue", [0x9203] = "BrightnessValue",
            [0x9204] = "ExposureBiasValue", [0x9205] = "MaxApertureValue", [0x9206] = "SubjectDistance",
            [0x9207] = "MeteringMode", [0x9208] = "LightSource", [0x9209] = "Flash", [0x920A] = "FocalLength",
            [0x9211] = "ImageNumber", [0x9212] = "SecurityClassification", [0x9213] = "ImageHistory",
            [0x9214] = "SubjectArea", [0x9217] = "SensingMethod2", [0x927C] = "MakerNote", [0x9286] = "UserComment",
            [0x9290] = "SubSecTime", [0x9291] = "SubSecTimeOriginal", [0x9292] = "SubSecTimeDigitized",
            [0x9400] = "Temperature", [0x9401] = "Humidity", [0x9402] = "Pressure", [0x9403] = "WaterDepth",
            [0x9404] = "Acceleration", [0x9405] = "CameraElevationAngle", [0x9C9B] = "XPTitle", [0x9C9C] = "XPComment",
            [0x9C9D] = "XPAuthor", [0x9C9E] = "XPKeywords", [0x9C9F] = "XPSubject", [0xA000] = "FlashpixVersion",
            [0xA001] = "ColorSpace", [0xA002] = "PixelXDimension", [0xA003] = "PixelYDimension",
            [0xA004] = "RelatedSoundFile", [0xA005] = "InteroperabilityIFDPointer", [0xA20B] = "FlashEnergy",
            [0xA20C] = "SpatialFrequencyResponse", [0xA20E] = "FocalPlaneXResolution", [0xA20F] = "FocalPlaneYResolution",
            [0xA210] = "FocalPlaneResolutionUnit", [0xA214] = "SubjectLocation", [0xA215] = "ExposureIndex",
            [0xA217] = "SensingMethod", [0xA300] = "FileSource", [0xA301] = "SceneType", [0xA302] = "CFAPattern",
            [0xA401] = "CustomRendered", [0xA402] = "ExposureMode", [0xA403] = "WhiteBalance",
            [0xA404] = "DigitalZoomRatio", [0xA405] = "FocalLengthIn35mmFilm", [0xA406] = "SceneCaptureType",
            [0xA407] = "GainControl", [0xA408] = "Contrast", [0xA409] = "Saturation", [0xA40A] = "Sharpness",
            [0xA40B] = "DeviceSettingDescription", [0xA40C] = "SubjectDistanceRange", [0xA420] = "ImageUniqueID",
            [0xA430] = "CameraOwnerName", [0xA431] = "BodySerialNumber", [0xA432] = "LensSpecification",
            [0xA433] = "LensMake", [0xA434] = "LensModel", [0xA435] = "LensSerialNumber", [0xA436] = "ImageTitle",
            [0xA437] = "Photographer", [0xA438] = "ImageEditor", [0xA439] = "CameraFirmware",
            [0xA43A] = "RAWDevelopingSoftware", [0xA43B] = "ImageEditingSoftware", [0xA43C] = "MetadataEditingSoftware",
            [0xA460] = "CompositeImage", [0xA461] = "SourceImageNumberOfCompositeImage",
            [0xA462] = "SourceExposureTimesOfCompositeImage", [0xA500] = "Gamma", [0xC4A5] = "PrintImageMatching",
            [0xC612] = "DNGVersion", [0xC613] = "DNGBackwardVersion", [0xC614] = "UniqueCameraModel",
            [0xC62F] = "CameraSerialNumber", [0xC630] = "LensInfo", [0xC615] = "LocalizedCameraModel",
            [0xC621] = "ColorMatrix1", [0xC622] = "ColorMatrix2", [0xC623] = "CameraCalibration1", [0xC624] = "CameraCalibration2",
            [0xC627] = "AnalogBalance", [0xC628] = "AsShotNeutral", [0xC62A] = "BaselineExposure", [0xC62B] = "BaselineNoise",
            [0xC62C] = "BaselineSharpness", [0xC62E] = "LinearResponseLimit", [0xC633] = "ShadowScale", [0xC634] = "DNGPrivateData",
            [0xC65A] = "CalibrationIlluminant1", [0xC65B] = "CalibrationIlluminant2", [0xC65D] = "RawDataUniqueID",
            [0xC68B] = "OriginalRawFileName", [0xEA1C] = "Padding", [0xEA1D] = "OffsetSchema",
            // GeoTIFF
            [0x830E] = "ModelPixelScale", [0x8482] = "ModelTiepoint", [0x85D8] = "ModelTransformation",
            [0x87AF] = "GeoKeyDirectory", [0x87B0] = "GeoDoubleParams", [0x87B1] = "GeoAsciiParams",
            [0xA480] = "GDALMetadata", [0xA481] = "GDALNoData", [0x935C] = "ImageSourceData",
        };

        static readonly Dictionary<ushort, string> Gps = new()
        {
            [0x0000] = "GPSVersionID", [0x0001] = "GPSLatitudeRef", [0x0002] = "GPSLatitude", [0x0003] = "GPSLongitudeRef",
            [0x0004] = "GPSLongitude", [0x0005] = "GPSAltitudeRef", [0x0006] = "GPSAltitude", [0x0007] = "GPSTimeStamp",
            [0x0008] = "GPSSatellites", [0x0009] = "GPSStatus", [0x000A] = "GPSMeasureMode", [0x000B] = "GPSDOP",
            [0x000C] = "GPSSpeedRef", [0x000D] = "GPSSpeed", [0x000E] = "GPSTrackRef", [0x000F] = "GPSTrack",
            [0x0010] = "GPSImgDirectionRef", [0x0011] = "GPSImgDirection", [0x0012] = "GPSMapDatum",
            [0x0013] = "GPSDestLatitudeRef", [0x0014] = "GPSDestLatitude", [0x0015] = "GPSDestLongitudeRef",
            [0x0016] = "GPSDestLongitude", [0x0017] = "GPSDestBearingRef", [0x0018] = "GPSDestBearing",
            [0x0019] = "GPSDestDistanceRef", [0x001A] = "GPSDestDistance", [0x001B] = "GPSProcessingMethod",
            [0x001C] = "GPSAreaInformation", [0x001D] = "GPSDateStamp", [0x001E] = "GPSDifferential",
            [0x001F] = "GPSHPositioningError",
        };

        static readonly Dictionary<ushort, string> Interop = new()
        {
            [0x0001] = "InteroperabilityIndex", [0x0002] = "InteroperabilityVersion", [0x1000] = "RelatedImageFileFormat",
            [0x1001] = "RelatedImageWidth", [0x1002] = "RelatedImageLength",
        };
    }
}
