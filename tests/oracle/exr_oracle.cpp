// Fixture helper for the OpenEXR codec tests: uses the reference library (libOpenEXR, the C++ API) to write
// variants the python bindings cannot produce (luminance/chroma, all mip/rip levels, deep data) and to produce
// references for them (RGBA interface, level reads, deep samples, deep flattening). Only gen_exr.py runs it.
//
// build (Debian: apt install libopenexr-dev), see gen_exr.py:
//   g++ -O1 -std=c++17 -I/usr/include/OpenEXR -I/usr/include/Imath exr_oracle.cpp -o exr_oracle -lOpenEXR -lImath -lIex
#include <ImfArray.h>
#include <ImfChannelList.h>
#include <ImfChromaticities.h>
#include <ImfCompositeDeepScanLine.h>
#include <ImfDeepFrameBuffer.h>
#include <ImfDeepScanLineInputPart.h>
#include <ImfDeepScanLineOutputPart.h>
#include <ImfDeepTiledInputPart.h>
#include <ImfDeepTiledOutputPart.h>
#include <ImfFrameBuffer.h>
#include <ImfHeader.h>
#include <ImfInputPart.h>
#include <ImfMultiPartInputFile.h>
#include <ImfMultiPartOutputFile.h>
#include <ImfOutputPart.h>
#include <ImfPartType.h>
#include <ImfRgbaFile.h>
#include <ImfStandardAttributes.h>
#include <ImfTiledInputPart.h>
#include <ImfTiledOutputPart.h>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <string>
#include <vector>

using namespace Imf;
using namespace Imath;
using std::string;
using std::vector;

static void writeRef (const char* path, int w, int h, int c, int type, const void* data, size_t bytes)
{
    FILE* f = fopen (path, "wb");
    if (!f) { perror (path); exit (1); }
    int hdr[4] = {w, h, c, type};
    fwrite ("PXRF", 1, 4, f);
    fwrite (hdr, 4, 4, f);
    fwrite (data, 1, bytes, f);
    fclose (f);
}

static Compression compressionByName (const string& n)
{
    static const std::map<string, Compression> m = {
        {"none", NO_COMPRESSION}, {"rle", RLE_COMPRESSION}, {"zips", ZIPS_COMPRESSION}, {"zip", ZIP_COMPRESSION},
        {"piz", PIZ_COMPRESSION}, {"pxr24", PXR24_COMPRESSION}, {"b44", B44_COMPRESSION}, {"b44a", B44A_COMPRESSION},
        {"dwaa", DWAA_COMPRESSION}, {"dwab", DWAB_COMPRESSION}};
    auto it = m.find (n);
    if (it == m.end ()) { fprintf (stderr, "unknown compression %s\n", n.c_str ()); exit (1); }
    return it->second;
}

// deterministic pseudo random numbers, independent of the C library
struct Rng
{
    uint32_t s;
    explicit Rng (uint32_t seed) : s (seed * 2654435761u + 12345u) {}
    uint32_t next () { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
    float unit () { return (next () >> 8) * (1.0f / 16777216.0f); }
};

static float pattern (int x, int y, int c, Rng& r)
{
    double v = 0.5 + 0.5 * std::sin (x / (7.0 + 3 * c) + c) * std::cos (y / (5.0 + 2 * c));
    v = 0.6 * v + 0.3 * (((x / 8 + y / 8 + c) % 2)) + 0.1 * r.unit ();
    return float (v);
}

// rgba <in> <out.ref> : what the RGBA interface returns (half RGBA), which converts luminance/chroma files
static int cmdRgba (int argc, char** argv)
{
    RgbaInputFile in (argv[2]);
    Box2i dw = in.dataWindow ();
    int w = dw.max.x - dw.min.x + 1, h = dw.max.y - dw.min.y + 1;
    Array2D<Rgba> px (h, w);
    in.setFrameBuffer (&px[0][0] - dw.min.x - (long) dw.min.y * w, 1, w);
    in.readPixels (dw.min.y, dw.max.y);
    writeRef (argv[3], w, h, 4, 6, &px[0][0], (size_t) w * h * 8);
    return 0;
}

// write-yc <out> <w> <h> <mode: yc|yca|y|ya> <compression> <xmin> <ymin> <seed> [chroma]
// "chroma" adds a chromaticities attribute with non default primaries (changes the luma weights)
static int cmdWriteYc (int argc, char** argv)
{
    int w = atoi (argv[3]), h = atoi (argv[4]);
    string mode = argv[5];
    int x0 = atoi (argv[7]), y0 = atoi (argv[8]);
    Rng rng (atoi (argv[9]));
    Header hd (Box2i (V2i (0, 0), V2i (w + 9, h + 4)), Box2i (V2i (x0, y0), V2i (x0 + w - 1, y0 + h - 1)), 1, V2f (0, 0), 1,
               INCREASING_Y, compressionByName (argv[6]));
    if (argc > 10 && string (argv[10]) == "chroma")
        addChromaticities (hd, Chromaticities (V2f (0.7347f, 0.2653f), V2f (0.0f, 1.0f), V2f (0.0001f, -0.077f), V2f (0.32168f, 0.33767f)));
    RgbaChannels ch = mode == "yc" ? WRITE_YC : mode == "yca" ? WRITE_YCA : mode == "y" ? WRITE_Y : WRITE_YA;
    Array2D<Rgba> px (h, w);
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            Rgba& p = px[y][x];
            p.r = pattern (x, y, 0, rng) * 1.7f; p.g = pattern (x, y, 1, rng); p.b = pattern (x, y, 2, rng) * 0.6f;
            p.a = pattern (x, y, 3, rng);
            if ((x / 9 + y / 7) % 5 == 0) p.r = p.g = p.b;                       // grey patches
            if ((x / 11 + y / 6) % 7 == 0) { p.r = 3.5f; p.g = 0.02f; p.b = 0.01f; }  // saturated patches
            if (x % 23 == 5 && y % 19 == 3) { p.r = 0; p.g = 0; p.b = 0; }
        }
    RgbaOutputFile out (argv[2], hd, ch);
    out.setFrameBuffer (&px[0][0] - x0 - (long) y0 * w, 1, w);
    out.writePixels (h);
    return 0;
}

// rgba-to-yc <in> <out> <compression> : re-save through the RGBA interface as luminance/chroma (with A if present)
static int cmdRgbaToYc (int argc, char** argv)
{
    RgbaInputFile in (argv[2]);
    Box2i dw = in.dataWindow ();
    int w = dw.max.x - dw.min.x + 1, h = dw.max.y - dw.min.y + 1;
    Array2D<Rgba> px (h, w);
    in.setFrameBuffer (&px[0][0] - dw.min.x - (long) dw.min.y * w, 1, w);
    in.readPixels (dw.min.y, dw.max.y);
    Header hd (in.displayWindow (), dw, 1, V2f (0, 0), 1, INCREASING_Y, compressionByName (argv[4]));
    if (hasChromaticities (in.header ())) addChromaticities (hd, chromaticities (in.header ()));
    RgbaOutputFile out (argv[3], hd, (in.channels () & WRITE_A) ? WRITE_YCA : WRITE_YC);
    out.setFrameBuffer (&px[0][0] - dw.min.x - (long) dw.min.y * w, 1, w);
    out.writePixels (h);
    return 0;
}

static vector<string> channelNames (const Header& h)
{
    vector<string> n;
    for (auto i = h.channels ().begin (); i != h.channels ().end (); ++i) n.push_back (i.name ());
    return n;
}

// Interleaves channels by the library rule: all HALF -> float16, all UINT -> uint32, otherwise float32.
static void writeChannels (const char* path, int w, int h, const vector<PixelType>& types, const vector<vector<char>>& data)
{
    bool allHalf = true, allUint = true;
    for (PixelType t : types) { allHalf &= t == HALF; allUint &= t == UINT; }
    int c = (int) types.size ();
    size_t n = (size_t) w * h;
    if (allHalf)
    {
        vector<half> o (n * c);
        for (int k = 0; k < c; k++) for (size_t i = 0; i < n; i++) o[i * c + k] = ((const half*) data[k].data ())[i];
        writeRef (path, w, h, c, 6, o.data (), o.size () * 2);
    }
    else if (allUint)
    {
        vector<unsigned> o (n * c);
        for (int k = 0; k < c; k++) for (size_t i = 0; i < n; i++) o[i * c + k] = ((const unsigned*) data[k].data ())[i];
        writeRef (path, w, h, c, 2, o.data (), o.size () * 4);
    }
    else
    {
        vector<float> o (n * c);
        for (int k = 0; k < c; k++)
            for (size_t i = 0; i < n; i++)
                o[i * c + k] = types[k] == HALF ? float (((const half*) data[k].data ())[i])
                             : types[k] == UINT ? float (((const unsigned*) data[k].data ())[i]) : ((const float*) data[k].data ())[i];
        writeRef (path, w, h, c, 7, o.data (), o.size () * 4);
    }
}

static size_t typeSize (PixelType t) { return t == HALF ? 2 : 4; }

// level <in> <part> <lx> <ly> <out.ref> [names...] : channels of one level of a tiled part (default: all, file order)
static int cmdLevel (int argc, char** argv)
{
    MultiPartInputFile mp (argv[2]);
    TiledInputPart in (mp, atoi (argv[3]));
    int lx = atoi (argv[4]), ly = atoi (argv[5]);
    vector<string> names;
    for (int i = 7; i < argc; i++) names.push_back (argv[i]);
    if (names.empty ()) names = channelNames (in.header ());
    Box2i dw = in.dataWindowForLevel (lx, ly);
    int w = dw.max.x - dw.min.x + 1, h = dw.max.y - dw.min.y + 1;
    vector<PixelType> types;
    vector<vector<char>> data;
    FrameBuffer fb;
    for (auto& n : names)
    {
        PixelType t = in.header ().channels ()[n.c_str ()].type;
        types.push_back (t);
        data.emplace_back ((size_t) w * h * typeSize (t));
    }
    for (size_t k = 0; k < names.size (); k++)
    {
        size_t s = typeSize (types[k]);
        fb.insert (names[k].c_str (), Slice (types[k], data[k].data () - (dw.min.x + (long) dw.min.y * w) * (long) s, s, s * w));
    }
    in.setFrameBuffer (fb);
    in.readTiles (0, in.numXTiles (lx) - 1, 0, in.numYTiles (ly) - 1, lx, ly);
    writeChannels (argv[6], w, h, types, data);
    return 0;
}

// write-levels <out> <mipmap|ripmap> <down|up> <w> <h> <tw> <th> <compression> <xmin> <ymin>
// R,G half, Z float, id uint; every level has its own content (level numbers are mixed into the pattern)
static int cmdWriteLevels (int argc, char** argv)
{
    int w = atoi (argv[5]), h = atoi (argv[6]), x0 = atoi (argv[10]), y0 = atoi (argv[11]);
    Header hd (Box2i (V2i (0, 0), V2i (w - 1, h - 1)), Box2i (V2i (x0, y0), V2i (x0 + w - 1, y0 + h - 1)));
    hd.compression () = compressionByName (argv[9]);
    hd.channels ().insert ("R", Channel (HALF));
    hd.channels ().insert ("G", Channel (HALF));
    hd.channels ().insert ("Z", Channel (FLOAT));
    hd.channels ().insert ("id", Channel (UINT));
    bool rip = string (argv[3]) == "ripmap";
    hd.setTileDescription (TileDescription (atoi (argv[7]), atoi (argv[8]), rip ? RIPMAP_LEVELS : MIPMAP_LEVELS,
                                            string (argv[4]) == "up" ? ROUND_UP : ROUND_DOWN));
    hd.setType (TILEDIMAGE);
    const Header* hp = &hd;
    MultiPartOutputFile mp (argv[2], hp, 1);
    TiledOutputPart out (mp, 0);
    for (int ly = 0; ly < out.numYLevels (); ly++)
        for (int lx = 0; lx < out.numXLevels (); lx++)
        {
            if (!rip && lx != ly) continue;
            Box2i dw = out.dataWindowForLevel (lx, ly);
            int lw = dw.max.x - dw.min.x + 1, lh = dw.max.y - dw.min.y + 1;
            vector<half> r (lw * lh), g (lw * lh);
            vector<float> z (lw * lh);
            vector<unsigned> id (lw * lh);
            Rng rng (lx * 31 + ly * 7 + 1);
            for (int y = 0; y < lh; y++)
                for (int x = 0; x < lw; x++)
                {
                    int i = y * lw + x;
                    r[i] = pattern (x << lx, y << ly, 0, rng) + lx;
                    g[i] = pattern (x << lx, y << ly, 1, rng) + ly * 0.5f;
                    z[i] = pattern (x, y, 2, rng) * 100.0f + lx * 1000 + ly * 10000;
                    id[i] = (unsigned) (x + y * 1000 + lx * 1000000 + ly * 10000000);
                }
            long o = dw.min.x + (long) dw.min.y * lw;
            FrameBuffer fb;
            fb.insert ("R", Slice (HALF, (char*) (r.data () - o), 2, 2 * lw));
            fb.insert ("G", Slice (HALF, (char*) (g.data () - o), 2, 2 * lw));
            fb.insert ("Z", Slice (FLOAT, (char*) (z.data () - o), 4, 4 * lw));
            fb.insert ("id", Slice (UINT, (char*) (id.data () - o), 4, 4 * lw));
            out.setFrameBuffer (fb);
            out.writeTiles (0, out.numXTiles (lx) - 1, 0, out.numYTiles (ly) - 1, lx, ly);
        }
    return 0;
}

// ---- deep data

struct DeepPart
{
    int w = 0, h = 0;
    vector<string> names;
    vector<PixelType> types;
    vector<unsigned> counts;            // per pixel
    vector<vector<char>> samples;       // per channel, all samples in pixel order
    size_t total = 0;
};

template <class Part> static void setupDeep (Part& in, DeepPart& d, Box2i dw, vector<vector<char*>>& ptrs, DeepFrameBuffer& fb)
{
    d.w = dw.max.x - dw.min.x + 1; d.h = dw.max.y - dw.min.y + 1;
    long o = dw.min.x + (long) dw.min.y * d.w;
    d.counts.assign ((size_t) d.w * d.h, 0);
    fb.insertSampleCountSlice (Slice (UINT, (char*) (d.counts.data () - o), 4, 4 * d.w));
    d.names = channelNames (in.header ());
    ptrs.resize (d.names.size ());
    for (size_t k = 0; k < d.names.size (); k++)
    {
        PixelType t = in.header ().channels ()[d.names[k].c_str ()].type;
        if (t == HALF) t = FLOAT;       // read halves as float (exact), keeps the dump format simple
        d.types.push_back (t);
        ptrs[k].assign ((size_t) d.w * d.h, nullptr);
        fb.insert (d.names[k].c_str (), DeepSlice (t, (char*) (ptrs[k].data () - o), sizeof (char*), sizeof (char*) * d.w, 4));
    }
}

static void allocDeep (DeepPart& d, vector<vector<char*>>& ptrs)
{
    d.total = 0;
    for (unsigned c : d.counts) d.total += c;
    d.samples.resize (d.names.size ());
    for (size_t k = 0; k < d.names.size (); k++)
    {
        d.samples[k].assign (d.total * 4 + 4, 0);
        size_t pos = 0;
        for (size_t i = 0; i < d.counts.size (); i++) { ptrs[k][i] = d.samples[k].data () + pos * 4; pos += d.counts[i]; }
    }
}

static DeepPart readDeep (MultiPartInputFile& mp, int part, int lx, int ly)
{
    DeepPart d;
    vector<vector<char*>> ptrs;
    DeepFrameBuffer fb;
    if (mp.header (part).type () == DEEPTILE)
    {
        DeepTiledInputPart in (mp, part);
        Box2i dw = in.dataWindowForLevel (lx, ly);
        setupDeep (in, d, dw, ptrs, fb);
        in.setFrameBuffer (fb);
        in.readPixelSampleCounts (0, in.numXTiles (lx) - 1, 0, in.numYTiles (ly) - 1, lx, ly);
        allocDeep (d, ptrs);
        in.readTiles (0, in.numXTiles (lx) - 1, 0, in.numYTiles (ly) - 1, lx, ly);
    }
    else
    {
        DeepScanLineInputPart in (mp, part);
        Box2i dw = in.header ().dataWindow ();
        setupDeep (in, d, dw, ptrs, fb);
        in.setFrameBuffer (fb);
        in.readPixelSampleCounts (dw.min.y, dw.max.y);
        allocDeep (d, ptrs);
        in.readPixels (dw.min.y, dw.max.y);
    }
    return d;
}

// deep <in> <part> <lx> <ly> <out.bin> : raw deep samples as read by the library.
// 'PXDP' w h nchan total | counts u32[w*h] | per channel: name\0, type u8 (0 uint32, 2 float32), samples[total]
static int cmdDeep (int argc, char** argv)
{
    MultiPartInputFile mp (argv[2]);
    DeepPart d = readDeep (mp, atoi (argv[3]), atoi (argv[4]), atoi (argv[5]));
    FILE* f = fopen (argv[6], "wb");
    int hdr[4] = {d.w, d.h, (int) d.names.size (), (int) d.total};
    fwrite ("PXDP", 1, 4, f);
    fwrite (hdr, 4, 4, f);
    fwrite (d.counts.data (), 4, d.counts.size (), f);
    for (size_t k = 0; k < d.names.size (); k++)
    {
        fwrite (d.names[k].c_str (), 1, d.names[k].size () + 1, f);
        fputc (d.types[k] == UINT ? 0 : 2, f);
        fwrite (d.samples[k].data (), 4, d.total, f);
    }
    fclose (f);
    return 0;
}

// flatten <in> <part> <out.ref> names... : the library's deep compositing (deep scan line parts), float32
static int cmdFlatten (int argc, char** argv)
{
    MultiPartInputFile mp (argv[2]);
    DeepScanLineInputPart in (mp, atoi (argv[3]));
    CompositeDeepScanLine comp;
    comp.addSource (&in);
    Box2i dw = comp.dataWindow ();
    int w = dw.max.x - dw.min.x + 1, h = dw.max.y - dw.min.y + 1, c = argc - 5;
    vector<float> px ((size_t) w * h * c);
    FrameBuffer fb;
    for (int k = 0; k < c; k++)
        fb.insert (argv[5 + k], Slice (FLOAT, (char*) (px.data () + k - ((long) dw.min.x + (long) dw.min.y * w) * c), 4 * c, 4 * c * w));
    comp.setFrameBuffer (fb);
    comp.readPixels (dw.min.y, dw.max.y);
    writeRef (argv[4], w, h, c, 7, px.data (), px.size () * 4);
    return 0;
}

// write-deep <out> <scan|tile|multi> <w> <h> <compression> <variant> <seed>
// variants: plain (A,Z + R,G,B,id; unsorted), sorted (same, front to back), zback (adds ZBack, sorted non overlapping
//           volumes), overlap (unsorted overlapping volumes), noalpha (Z,R only). "multi" writes a flat part, a deep scan line part and a deep tiled part.
struct DeepData
{
    int w, h;
    vector<unsigned> counts;
    std::map<string, vector<float>> f;      // HALF and FLOAT channels
    vector<unsigned> id;
    std::map<string, vector<char*>> ptrs;
    std::map<string, vector<half>> halves;   // HALF channels in their file type (deep output does not convert)
};

static void makeDeep (DeepData& d, int w, int h, const string& variant, int seed)
{
    Rng rng (seed);
    d.w = w; d.h = h;
    d.counts.resize (w * h);
    size_t total = 0;
    for (int i = 0; i < w * h; i++)
    {
        unsigned r = rng.next () % 10;
        d.counts[i] = r < 2 ? 0 : r < 5 ? 1 : r - 3;        // 0..6 samples
        total += d.counts[i];
    }
    for (const char* n : {"A", "Z", "ZBack", "R", "G", "B"}) d.f[n].resize (total + 1);
    d.id.resize (total + 1);
    size_t p = 0;
    for (int i = 0; i < w * h; i++)
    {
        float z = 1 + rng.unit () * 3;
        for (unsigned s = 0; s < d.counts[i]; s++, p++)
        {
            float a = rng.next () % 7 == 0 ? 1.0f : rng.next () % 5 == 0 ? 0.0f : float (half (rng.unit ()));
            float thick = rng.next () % 3 == 0 ? 0 : rng.unit () * 2;
            d.f["A"][p] = a;
            d.f["Z"][p] = z;
            d.f["ZBack"][p] = z + thick;
            // premultiplied colour (with some emission where alpha is zero)
            d.f["R"][p] = float (half (rng.unit () * (a > 0 ? a : 0.3f)));
            d.f["G"][p] = float (half (rng.unit () * (a > 0 ? a : 0.3f)));
            d.f["B"][p] = float (half (rng.unit () * (a > 0 ? a : 0.3f)));
            d.id[p] = rng.next ();
            // overlap: the next sample may start inside this one, or in front of it (unsorted);
            // plain / noalpha: point samples in no particular order; sorted / zback: front to back, no overlap
            if (variant == "overlap") z += rng.next () % 5 == 0 ? -0.8f : rng.next () % 2 ? thick * 0.5f : thick + rng.unit ();
            else if (variant == "plain" || variant == "noalpha") z = (rng.next () % 4 == 0 ? z - 0.37f * (s + 1) : z + rng.unit ());
            else z += thick + rng.unit () + 0.01f;
        }
    }
}

static void deepHeader (Header& hd, const string& variant)
{
    if (variant != "noalpha") hd.channels ().insert ("A", Channel (HALF));
    hd.channels ().insert ("Z", Channel (FLOAT));
    if (variant == "zback" || variant == "overlap") hd.channels ().insert ("ZBack", Channel (FLOAT));
    hd.channels ().insert ("R", Channel (HALF));
    if (variant != "noalpha")
    {
        hd.channels ().insert ("G", Channel (HALF));
        hd.channels ().insert ("B", Channel (HALF));
        hd.channels ().insert ("id", Channel (UINT));
    }
}

static void deepFrameBuffer (DeepFrameBuffer& fb, DeepData& d, const Header& hd, Box2i dw)
{
    long o = dw.min.x + (long) dw.min.y * d.w;
    fb.insertSampleCountSlice (Slice (UINT, (char*) (d.counts.data () - o), 4, 4 * d.w));
    for (auto i = hd.channels ().begin (); i != hd.channels ().end (); ++i)
    {
        string n = i.name ();
        PixelType t = i.channel ().type;
        char* base = n == "id" ? (char*) d.id.data () : (char*) d.f[n].data ();
        if (t == HALF)
        {
            auto& hv = d.halves[n];
            hv.assign (d.f[n].begin (), d.f[n].end ());
            base = (char*) hv.data ();
        }
        size_t ss = t == HALF ? 2 : 4;
        auto& pp = d.ptrs[n];
        pp.resize (d.w * d.h);
        size_t pos = 0;
        for (int k = 0; k < d.w * d.h; k++) { pp[k] = base + pos * ss; pos += d.counts[k]; }
        fb.insert (n.c_str (), DeepSlice (t, (char*) (pp.data () - o), sizeof (char*), sizeof (char*) * d.w, ss));
    }
}

static int cmdWriteDeep (int argc, char** argv)
{
    string kind = argv[3], variant = argv[7];
    int w = atoi (argv[4]), h = atoi (argv[5]), seed = atoi (argv[8]);
    Compression comp = compressionByName (argv[6]);
    Box2i dw (V2i (-3, 2), V2i (-3 + w - 1, 2 + h - 1)), disp (V2i (0, 0), V2i (w + 5, h + 5));
    vector<Header> headers;
    if (kind == "multi")
    {
        Header flat (disp, dw);
        flat.channels ().insert ("R", Channel (HALF));
        flat.compression () = ZIP_COMPRESSION;
        flat.setType (SCANLINEIMAGE);
        flat.setName ("flat");
        headers.push_back (flat);
    }
    if (kind != "tile")
    {
        Header hd (disp, dw);
        deepHeader (hd, variant);
        hd.compression () = comp;
        hd.setType (DEEPSCANLINE);
        hd.setName ("deepscan");
        headers.push_back (hd);
    }
    if (kind != "scan")
    {
        Header hd (disp, dw);
        deepHeader (hd, variant);
        hd.compression () = comp;
        hd.setType (DEEPTILE);
        hd.setName ("deeptile");
        hd.setTileDescription (TileDescription (8, 5, ONE_LEVEL));
        headers.push_back (hd);
    }
    MultiPartOutputFile mp (argv[2], headers.data (), (int) headers.size ());
    int part = 0;
    if (kind == "multi")
    {
        vector<half> r (w * h);
        Rng rng (seed + 9);
        for (auto& v : r) v = rng.unit ();
        OutputPart out (mp, part++);
        FrameBuffer fb;
        fb.insert ("R", Slice (HALF, (char*) (r.data () - (dw.min.x + (long) dw.min.y * w)), 2, 2 * w));
        out.setFrameBuffer (fb);
        out.writePixels (h);
    }
    if (kind != "tile")
    {
        DeepData d;
        makeDeep (d, w, h, variant, seed);
        DeepFrameBuffer fb;
        deepFrameBuffer (fb, d, headers[part], dw);
        DeepScanLineOutputPart out (mp, part++);
        out.setFrameBuffer (fb);
        out.writePixels (h);
    }
    if (kind != "scan")
    {
        DeepData d;
        makeDeep (d, w, h, variant, seed + (kind == "multi" ? 1 : 0));
        DeepFrameBuffer fb;
        deepFrameBuffer (fb, d, headers[part], dw);
        DeepTiledOutputPart out (mp, part++);
        out.setFrameBuffer (fb);
        out.writeTiles (0, out.numXTiles (0) - 1, 0, out.numYTiles (0) - 1, 0, 0);
    }
    return 0;
}

int main (int argc, char** argv)
{
    try
    {
        string c = argc > 1 ? argv[1] : "";
        if (c == "rgba") return cmdRgba (argc, argv);
        if (c == "write-yc") return cmdWriteYc (argc, argv);
        if (c == "rgba-to-yc") return cmdRgbaToYc (argc, argv);
        if (c == "level") return cmdLevel (argc, argv);
        if (c == "write-levels") return cmdWriteLevels (argc, argv);
        if (c == "deep") return cmdDeep (argc, argv);
        if (c == "flatten") return cmdFlatten (argc, argv);
        if (c == "write-deep") return cmdWriteDeep (argc, argv);
        fprintf (stderr, "unknown command\n");
        return 2;
    }
    catch (const std::exception& e)
    {
        fprintf (stderr, "exr_oracle: %s\n", e.what ());
        return 1;
    }
}
