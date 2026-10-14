using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PixCodec
{
    /// <summary>Position and motion from the GPS directory (or XMP / OpenEXR attributes when there is none).</summary>
    public sealed class GpsInfo
    {
        /// <summary>Latitude in degrees, north positive.</summary>
        public double? Latitude { get; internal set; }
        /// <summary>Longitude in degrees, east positive.</summary>
        public double? Longitude { get; internal set; }
        /// <summary>Altitude in metres, negative below the reference (<see cref="AltitudeReference"/>).</summary>
        public double? Altitude { get; internal set; }
        /// <summary>EXIF GPSAltitudeRef: 0 / 1 above / below sea level, 2 / 3 above / below the ellipsoid (EXIF 3.0).</summary>
        public int? AltitudeReference { get; internal set; }
        /// <summary>GPS time (UTC), from GPSDateStamp and GPSTimeStamp.</summary>
        public DateTime? TimeUtc { get; internal set; }
        /// <summary>Speed of the receiver in metres per second.</summary>
        public double? Speed { get; internal set; }
        /// <summary>Direction of movement in degrees (<see cref="TrackIsMagnetic"/>: magnetic north, else true north).</summary>
        public double? Track { get; internal set; }
        public bool TrackIsMagnetic { get; internal set; }
        /// <summary>Direction the camera points to, in degrees (<see cref="ImageDirectionIsMagnetic"/>: magnetic north).</summary>
        public double? ImageDirection { get; internal set; }
        public bool ImageDirectionIsMagnetic { get; internal set; }
        /// <summary>Bearing to the destination (the photographed object) in degrees.</summary>
        public double? DestinationBearing { get; internal set; }
        /// <summary>Dilution of precision.</summary>
        public double? Dop { get; internal set; }
        /// <summary>Horizontal positioning error in metres.</summary>
        public double? HorizontalError { get; internal set; }
        /// <summary>Vertical error in metres (XMP Camera:GPSZAccuracy).</summary>
        public double? VerticalError { get; internal set; }
        public string? MapDatum { get; internal set; }
        public string? Satellites { get; internal set; }
        /// <summary>"A" measurement active, "V" void.</summary>
        public string? Status { get; internal set; }
        /// <summary>"2" or "3" dimensional measurement.</summary>
        public string? MeasureMode { get; internal set; }
        /// <summary>Differential correction applied (EXIF GPSDifferential 1).</summary>
        public bool? Differential { get; internal set; }
        /// <summary>The positioning method ("GPS", "CELLID", "WLAN", "MANUAL", ...).</summary>
        public string? ProcessingMethod { get; internal set; }

        public override string ToString()
            => Latitude is double la && Longitude is double lo
                ? string.Format(CultureInfo.InvariantCulture, "{0:F6}, {1:F6}", la, lo) + (Altitude is double a ? string.Format(CultureInfo.InvariantCulture, ", {0:F1} m", a) : "")
                : "(no position)";
    }

    /// <summary>
    /// Attitude of the camera and the vehicle carrying it, as drones and some mapping cameras record it (DJI drone-dji,
    /// Parrot drone-parrot, Pix4D / MicaSense / Sentera Camera XMP namespaces). Angles in degrees: yaw clockwise from north,
    /// pitch positive up (DJI gimbal pitch -90 looks straight down), roll positive right.
    /// </summary>
    public sealed class CameraPose
    {
        /// <summary>The XMP namespace prefix the values came from ("drone-dji", "drone-parrot", "Camera", "GPano").</summary>
        public string Source { get; internal set; } = "";
        public double? CameraYaw { get; internal set; }
        public double? CameraPitch { get; internal set; }
        public double? CameraRoll { get; internal set; }
        public double? VehicleYaw { get; internal set; }
        public double? VehiclePitch { get; internal set; }
        public double? VehicleRoll { get; internal set; }
        /// <summary>Height above the take-off point in metres.</summary>
        public double? RelativeAltitude { get; internal set; }
        /// <summary>Altitude in metres as the vehicle reports it (DJI: above sea level or the ellipsoid, model dependent).</summary>
        public double? AbsoluteAltitude { get; internal set; }
        /// <summary>Vehicle speed in m/s (DJI FlightXSpeed / Y / Z).</summary>
        public double? SpeedX { get; internal set; }
        public double? SpeedY { get; internal set; }
        public double? SpeedZ { get; internal set; }
        /// <summary>RTK solution flag (DJI: 0 none, 16 single, 34 float, 50 fixed).</summary>
        public int? RtkFlag { get; internal set; }
        /// <summary>RTK standard deviations in metres (latitude, longitude, height).</summary>
        public double? RtkStdLatitude { get; internal set; }
        public double? RtkStdLongitude { get; internal set; }
        public double? RtkStdHeight { get; internal set; }
        /// <summary>Calibrated focal length in pixels and principal point (DJI CalibratedFocalLength / OpticalCenter).</summary>
        public double? CalibratedFocalLength { get; internal set; }
        public double? CalibratedOpticalCenterX { get; internal set; }
        public double? CalibratedOpticalCenterY { get; internal set; }
    }

    /// <summary>Google photo sphere (GPano) properties.</summary>
    public sealed class PanoramaInfo
    {
        public string? ProjectionType { get; internal set; }
        public int? FullWidth { get; internal set; }
        public int? FullHeight { get; internal set; }
        public int? CroppedWidth { get; internal set; }
        public int? CroppedHeight { get; internal set; }
        public int? CroppedLeft { get; internal set; }
        public int? CroppedTop { get; internal set; }
        public double? PoseHeading { get; internal set; }
        public double? PosePitch { get; internal set; }
        public double? PoseRoll { get; internal set; }
    }

    /// <summary>
    /// Descriptive metadata of an image file: the raw EXIF, XMP and IPTC data and comments, and the commonly used values
    /// taken from them (camera, capture settings, dates, GPS, drone attitude, description). When several sources hold a
    /// value, EXIF wins over XMP over IPTC over format specific text. Read with <see cref="ImageCodec.ReadMetadata(string)"/>.
    /// </summary>
    public sealed class PhotoMetadata
    {
        public ImageFormat Format { get; internal set; }

        // raw sources
        /// <summary>The EXIF directories (for TIFF files: the first page's directories).</summary>
        public ExifData? Exif { get; internal set; }
        public XmpData? Xmp { get; internal set; }
        public IptcData? Iptc { get; internal set; }
        public byte[]? IccProfile { get; internal set; }
        /// <summary>The ICC profile's description ("sRGB IEC61966-2.1", "Display P3", ...).</summary>
        public string? IccDescription { get; internal set; }
        /// <summary>JPEG COM, GIF comment extension and JPEG 2000 COM text.</summary>
        public IReadOnlyList<string> Comments { get; internal set; } = Array.Empty<string>();
        /// <summary>PNG tEXt / zTXt / iTXt chunks (key, text) and the PNG tIME stamp.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Text { get; internal set; } = Array.Empty<KeyValuePair<string, string>>();
        /// <summary>OpenEXR header attributes of the first part.</summary>
        public IReadOnlyList<ExrAttribute>? ExrAttributes { get; internal set; }
        /// <summary>The EXIF thumbnail (JPEG data), if any.</summary>
        public byte[]? Thumbnail { get; internal set; }

        // camera
        public string? Make { get; internal set; }
        public string? Model { get; internal set; }
        public string? SerialNumber { get; internal set; }
        public string? LensMake { get; internal set; }
        public string? LensModel { get; internal set; }
        public string? LensSerialNumber { get; internal set; }
        /// <summary>Lens focal length range (mm) and maximum aperture at min / max focal length.</summary>
        public double[]? LensSpecification { get; internal set; }
        public string? Software { get; internal set; }
        public string? OwnerName { get; internal set; }

        // capture
        /// <summary>When the picture was taken, in local time of the camera (with sub seconds when stored).</summary>
        public DateTime? DateTaken { get; internal set; }
        /// <summary>Offset of <see cref="DateTaken"/> from UTC, when the file says (EXIF OffsetTimeOriginal, XMP, IPTC).</summary>
        public TimeSpan? DateTakenOffset { get; internal set; }
        /// <summary><see cref="DateTaken"/> with its offset, when both are known.</summary>
        public DateTimeOffset? DateTakenWithOffset => DateTaken is DateTime t && DateTakenOffset is TimeSpan o ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), o) : null;
        public DateTime? DateDigitized { get; internal set; }
        public TimeSpan? DateDigitizedOffset { get; internal set; }
        /// <summary>Last modification of the file (EXIF DateTime, XMP ModifyDate, PNG tIME).</summary>
        public DateTime? DateModified { get; internal set; }
        public TimeSpan? DateModifiedOffset { get; internal set; }
        /// <summary>Exposure time in seconds.</summary>
        public double? ExposureTime { get; internal set; }
        public double? FNumber { get; internal set; }
        public int? Iso { get; internal set; }
        /// <summary>Focal length in mm.</summary>
        public double? FocalLength { get; internal set; }
        /// <summary>35 mm equivalent focal length in mm.</summary>
        public double? FocalLengthIn35mm { get; internal set; }
        /// <summary>Exposure compensation in EV.</summary>
        public double? ExposureBias { get; internal set; }
        /// <summary>EXIF Flash value (bit 0: fired).</summary>
        public int? Flash { get; internal set; }
        public bool? FlashFired => Flash is int f ? (f & 1) != 0 : null;
        /// <summary>EXIF ExposureProgram (1 manual, 2 normal, 3 aperture priority, 4 shutter priority, ...).</summary>
        public int? ExposureProgram { get; internal set; }
        /// <summary>EXIF MeteringMode (1 average, 2 centre weighted, 3 spot, 4 multi spot, 5 pattern, 6 partial).</summary>
        public int? MeteringMode { get; internal set; }
        /// <summary>EXIF WhiteBalance (0 auto, 1 manual).</summary>
        public int? WhiteBalance { get; internal set; }
        public double? DigitalZoomRatio { get; internal set; }
        /// <summary>Subject distance in metres.</summary>
        public double? SubjectDistance { get; internal set; }
        /// <summary>EXIF / TIFF orientation as stored (1..8), 0 if absent.</summary>
        public int Orientation { get; internal set; }

        // description
        public string? Title { get; internal set; }
        public string? Description { get; internal set; }
        public string? Artist { get; internal set; }
        public string? Copyright { get; internal set; }
        public IReadOnlyList<string> Keywords { get; internal set; } = Array.Empty<string>();
        /// <summary>Rating 0..5 (-1 rejected).</summary>
        public int? Rating { get; internal set; }
        public string? UserComment { get; internal set; }
        public string? City { get; internal set; }
        public string? State { get; internal set; }
        public string? Country { get; internal set; }
        public string? Sublocation { get; internal set; }
        public string? ImageUniqueId { get; internal set; }

        public GpsInfo? Gps { get; internal set; }
        public CameraPose? Pose { get; internal set; }
        public PanoramaInfo? Panorama { get; internal set; }

        /// <summary>Whether the file carries an HDR gain map description (XMP hdrgm).</summary>
        public bool HasGainMap { get; internal set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            void Add(string k, object? v) { if (v != null) sb.Append(k).Append(": ").Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append('\n'); }
            Add("Format", Format); Add("Make", Make); Add("Model", Model); Add("Lens", LensModel); Add("DateTaken", DateTaken);
            Add("ExposureTime", ExposureTime); Add("FNumber", FNumber); Add("ISO", Iso); Add("FocalLength", FocalLength);
            Add("GPS", Gps); Add("Title", Title); Add("Description", Description);
            return sb.ToString();
        }
    }
}
