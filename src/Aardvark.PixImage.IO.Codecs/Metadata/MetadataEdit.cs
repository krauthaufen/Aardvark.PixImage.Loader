using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs
{
    /// <summary>Descriptive fields of <see cref="MetadataEdit"/> that can be removed with <see cref="MetadataEdit.Remove"/>.</summary>
    public enum MetadataField
    {
        Make, Model, SerialNumber, LensMake, LensModel, LensSerialNumber, LensSpecification, Software, OwnerName,
        /// <summary>The capture date with its sub seconds and offset.</summary>
        DateTaken,
        /// <summary>Only the offset of the capture date (EXIF OffsetTimeOriginal).</summary>
        DateTakenOffset,
        DateDigitized, DateModified,
        ExposureTime, FNumber, Iso, FocalLength, FocalLengthIn35mm, ExposureBias, Flash, ExposureProgram, MeteringMode,
        WhiteBalance, DigitalZoomRatio, SubjectDistance, Orientation,
        Title, Description, Artist, Copyright, Keywords, Rating, UserComment, ImageUniqueId, City, State, Country, Sublocation,
    }

    /// <summary>The kind of an XMP array (rdf:Bag unordered, rdf:Seq ordered, rdf:Alt alternatives / languages).</summary>
    public enum XmpArrayType { Bag, Seq, Alt }

    /// <summary>
    /// GPS values to write (EXIF GPS directory). Only the values that are set are written; the others stay as they are
    /// (combine with <see cref="MetadataEdit.RemoveGps"/> to replace the whole directory).
    /// </summary>
    public sealed class GpsEdit
    {
        /// <summary>Latitude in degrees, north positive (-90..90).</summary>
        public double? Latitude { get; set; }
        /// <summary>Longitude in degrees, east positive (-180..180).</summary>
        public double? Longitude { get; set; }
        /// <summary>Altitude in metres, negative below the reference.</summary>
        public double? Altitude { get; set; }
        /// <summary>The altitude refers to the WGS-84 ellipsoid (EXIF 3.0 GPSAltitudeRef 2 / 3) instead of sea level.</summary>
        public bool AltitudeAboveEllipsoid { get; set; }
        /// <summary>Time of the GPS fix (converted to UTC unless its Kind is Utc or Unspecified, which is taken as UTC).</summary>
        public DateTime? TimeUtc { get; set; }
        /// <summary>Speed of the receiver in metres per second (written in km/h).</summary>
        public double? Speed { get; set; }
        /// <summary>Direction of movement in degrees (0..360).</summary>
        public double? Track { get; set; }
        public bool TrackIsMagnetic { get; set; }
        /// <summary>Direction the camera points to in degrees (0..360).</summary>
        public double? ImageDirection { get; set; }
        public bool ImageDirectionIsMagnetic { get; set; }
        /// <summary>Bearing to the destination in degrees.</summary>
        public double? DestinationBearing { get; set; }
        /// <summary>Horizontal positioning error in metres (EXIF GPSHPositioningError).</summary>
        public double? HorizontalError { get; set; }
        /// <summary>Dilution of precision.</summary>
        public double? Dop { get; set; }
        public string? MapDatum { get; set; }
        public string? Satellites { get; set; }
        /// <summary>"A" measurement active, "V" void.</summary>
        public string? Status { get; set; }
        /// <summary>"2" or "3" dimensional measurement.</summary>
        public string? MeasureMode { get; set; }
        /// <summary>Differential correction applied.</summary>
        public bool? Differential { get; set; }
        /// <summary>The positioning method ("GPS", "CELLID", "WLAN", "MANUAL", ...).</summary>
        public string? ProcessingMethod { get; set; }
    }

    /// <summary>
    /// A set of changes to the metadata of an image: descriptive values (camera, capture settings, dates, GPS, title ...),
    /// arbitrary EXIF tags, XMP properties and IPTC datasets, and removals. Properties left null are not touched.
    /// <para>
    /// Apply it to a file without re-encoding the pixels with <see cref="ImageCodec.WriteMetadata(ReadOnlySpan{byte}, MetadataEdit)"/>
    /// (JPEG, PNG, TIFF / BigTIFF, WebP, JPEG 2000), or to an <see cref="ImageMetadata"/> before encoding with
    /// <see cref="ApplyTo(ImageMetadata)"/>.
    /// </para>
    /// <para>
    /// Where values go: EXIF holds camera, exposure, dates, GPS and the description tags it has (ImageDescription, Artist,
    /// Copyright, ImageTitle); XMP always receives title, description, creator, rights, keywords, rating and location
    /// (dc, xmp and photoshop namespaces) and is updated wherever it already holds one of the EXIF values (exif:, tiff:,
    /// aux: properties). IPTC-IIM datasets are only updated where the file already carries an IPTC block. Removing a field
    /// removes it everywhere.
    /// </para>
    /// </summary>
    public sealed class MetadataEdit
    {
        // ------------------------------------------------------------------ removals

        /// <summary>
        /// Removes all EXIF, XMP and IPTC data, comments (JPEG COM) and text chunks (PNG) before the values of this edit are
        /// written. The ICC profile, the resolution and the orientation stay (orientation changes how the image is shown).
        /// TIFF files keep every structural and GeoTIFF tag.
        /// </summary>
        public bool RemoveAll { get; set; }

        /// <summary>Removes the GPS directory and the GPS / position properties of XMP (exif:GPS*, drone and camera namespaces).</summary>
        public bool RemoveGps { get; set; }

        /// <summary>Removes the EXIF thumbnail (IFD1).</summary>
        public bool RemoveThumbnail { get; set; }

        readonly List<MetadataField> _removed = new();
        /// <summary>Fields to remove (everywhere they are stored).</summary>
        public IReadOnlyList<MetadataField> RemovedFields => _removed;

        /// <summary>Removes the given fields from EXIF, XMP, IPTC and PNG text.</summary>
        public MetadataEdit Remove(params MetadataField[] fields)
        {
            foreach (var f in fields) if (!_removed.Contains(f)) _removed.Add(f);
            return this;
        }

        // ------------------------------------------------------------------ camera

        public string? Make { get; set; }
        public string? Model { get; set; }
        /// <summary>Camera body serial number (EXIF BodySerialNumber).</summary>
        public string? SerialNumber { get; set; }
        public string? LensMake { get; set; }
        public string? LensModel { get; set; }
        public string? LensSerialNumber { get; set; }
        /// <summary>Minimum and maximum focal length (mm), maximum aperture at the minimum and at the maximum focal length.</summary>
        public double[]? LensSpecification { get; set; }
        public string? Software { get; set; }
        /// <summary>EXIF CameraOwnerName.</summary>
        public string? OwnerName { get; set; }

        // ------------------------------------------------------------------ capture

        /// <summary>When the picture was taken, local time of the camera; sub seconds are written too (EXIF SubSecTimeOriginal).</summary>
        public DateTime? DateTaken { get; set; }
        /// <summary>Offset of <see cref="DateTaken"/> from UTC (EXIF OffsetTimeOriginal).</summary>
        public TimeSpan? DateTakenOffset { get; set; }
        public DateTime? DateDigitized { get; set; }
        public TimeSpan? DateDigitizedOffset { get; set; }
        /// <summary>Last modification (EXIF DateTime / XMP ModifyDate).</summary>
        public DateTime? DateModified { get; set; }
        public TimeSpan? DateModifiedOffset { get; set; }
        /// <summary>Exposure time in seconds.</summary>
        public double? ExposureTime { get; set; }
        public double? FNumber { get; set; }
        public int? Iso { get; set; }
        /// <summary>Focal length in mm.</summary>
        public double? FocalLength { get; set; }
        /// <summary>35 mm equivalent focal length in mm (whole millimetres).</summary>
        public int? FocalLengthIn35mm { get; set; }
        /// <summary>Exposure compensation in EV.</summary>
        public double? ExposureBias { get; set; }
        /// <summary>EXIF Flash value (bit 0: fired).</summary>
        public int? Flash { get; set; }
        public int? ExposureProgram { get; set; }
        public int? MeteringMode { get; set; }
        public int? WhiteBalance { get; set; }
        public double? DigitalZoomRatio { get; set; }
        /// <summary>Subject distance in metres.</summary>
        public double? SubjectDistance { get; set; }
        /// <summary>
        /// EXIF / TIFF orientation 1..8: how the stored pixels are to be turned for display. The pixels are not changed.
        /// </summary>
        public int? Orientation { get; set; }

        // ------------------------------------------------------------------ description

        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Artist { get; set; }
        public string? Copyright { get; set; }
        /// <summary>Keywords; replace all existing ones (XMP dc:subject, IPTC Keywords, Windows XPKeywords).</summary>
        public IReadOnlyList<string>? Keywords { get; set; }
        /// <summary>Rating 0..5, -1 rejected.</summary>
        public int? Rating { get; set; }
        public string? UserComment { get; set; }
        /// <summary>EXIF ImageUniqueID (32 hex digits).</summary>
        public string? ImageUniqueId { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? Country { get; set; }
        public string? Sublocation { get; set; }

        /// <summary>GPS values to write.</summary>
        public GpsEdit? Gps { get; set; }

        // ------------------------------------------------------------------ raw EXIF / XMP / IPTC

        internal readonly List<(ExifIfd Ifd, ushort Tag, ExifValue? Value)> RawExif = new();
        internal readonly List<(string Key, XmpValue? Value)> RawXmp = new();
        internal readonly List<(int Record, int Dataset, string[]? Values)> RawIptc = new();
        internal readonly Dictionary<string, string> Namespaces = new();

        /// <summary>
        /// Sets an EXIF tag. <paramref name="value"/>: a string (Ascii, Utf8), byte[] (Byte, Undefined), sbyte[], ushort / ushort[]
        /// or int / int[] (Short), uint / uint[] (Long), short[] (SShort), int[] (SLong), ExifRational / ExifRational[] or
        /// double / double[] (Rational, SRational), float / float[] (Float), double / double[] (Double). Directory structure tags
        /// (pointers, strip / tile layout, thumbnail location) cannot be set.
        /// </summary>
        public MetadataEdit SetExif(ExifIfd ifd, ushort tag, ExifType type, object value)
        {
            var v = ExifValue.From(type, value ?? throw new ArgumentNullException(nameof(value)));
            ExifEdit.CheckSettable(ifd, tag);
            RawExif.Add((ifd, tag, v));
            return this;
        }

        /// <summary>Removes an EXIF tag.</summary>
        public MetadataEdit RemoveExif(ExifIfd ifd, ushort tag)
        {
            ExifEdit.CheckSettable(ifd, tag);
            RawExif.Add((ifd, tag, null));
            return this;
        }

        /// <summary>
        /// Sets a simple XMP property. <paramref name="key"/>: "prefix:Name", or "prefix:Struct/prefix:Field" for a field of a
        /// structure. The prefix must be one of <see cref="XmpData.KnownNamespaces"/> or registered with <see cref="SetXmpNamespace"/>.
        /// </summary>
        public MetadataEdit SetXmp(string key, string value)
        {
            XmpEditor.CheckKey(key, this);
            RawXmp.Add((key, new XmpValue(null, new[] { value ?? throw new ArgumentNullException(nameof(value)) })));
            return this;
        }

        /// <summary>Sets an XMP array property (an Alt array gets its first value as the x-default language).</summary>
        public MetadataEdit SetXmp(string key, IEnumerable<string> values, XmpArrayType type)
        {
            XmpEditor.CheckKey(key, this);
            RawXmp.Add((key, new XmpValue(type, new List<string>(values ?? throw new ArgumentNullException(nameof(values))).ToArray())));
            return this;
        }

        /// <summary>Removes an XMP property ("prefix:Name", "prefix:Struct/prefix:Field"), or all of a namespace ("prefix:*").</summary>
        public MetadataEdit RemoveXmp(string key)
        {
            XmpEditor.CheckKey(key, this, allowWildcard: true);
            RawXmp.Add((key, null));
            return this;
        }

        /// <summary>Registers the namespace URI of a prefix used in <see cref="SetXmp(string, string)"/> keys.</summary>
        public MetadataEdit SetXmpNamespace(string prefix, string uri)
        {
            if (string.IsNullOrEmpty(prefix) || prefix.Contains(':') || string.IsNullOrEmpty(uri)) throw new ArgumentException("bad XMP namespace");
            Namespaces[prefix] = uri;
            return this;
        }

        /// <summary>
        /// Sets an IPTC-IIM dataset (all values; several for repeatable datasets such as 2:25 Keywords). IPTC is only written
        /// into files that already carry an IPTC block.
        /// </summary>
        public MetadataEdit SetIptc(int record, int dataset, params string[] values)
        {
            if (record is < 1 or > 9 || dataset is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(dataset));
            RawIptc.Add((record, dataset, values ?? throw new ArgumentNullException(nameof(values))));
            return this;
        }

        /// <summary>Removes an IPTC-IIM dataset.</summary>
        public MetadataEdit RemoveIptc(int record, int dataset)
        {
            RawIptc.Add((record, dataset, null));
            return this;
        }

        // ------------------------------------------------------------------ applying

        /// <summary>
        /// Applies the edit to the metadata of an image before encoding: <see cref="ImageMetadata.Exif"/> and
        /// <see cref="ImageMetadata.Xmp"/> are rewritten (created when needed) and <see cref="ImageMetadata.Orientation"/> is
        /// set when the edit sets one. IPTC has no place in <see cref="ImageMetadata"/> and is not written this way.
        /// </summary>
        public void ApplyTo(ImageMetadata metadata)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            var plan = MetadataPlan.Build(this);
            if (Orientation is int o) metadata.Orientation = o;
            else if (_removed.Contains(MetadataField.Orientation)) metadata.Orientation = 0;
            var exif = ExifEdit.EditBlob(metadata.Exif, plan, null);
            // the encoders write ImageMetadata.Orientation into the blob: keep them in step
            if (exif != null && Orientation == null && !_removed.Contains(MetadataField.Orientation) && metadata.Orientation is >= 1 and <= 8)
                exif = ExifTags.WithOrientation(exif, metadata.Orientation);
            metadata.Exif = exif;
            metadata.Xmp = XmpEditor.Edit(metadata.Xmp, plan, out _);
        }

        /// <summary>Applies the edit to a decoded image's metadata (created when the image has none).</summary>
        public void ApplyTo(RawImage image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            image.Metadata ??= new ImageMetadata();
            ApplyTo(image.Metadata);
        }

        internal bool Removes(MetadataField f) => _removed.Contains(f);
    }
}
