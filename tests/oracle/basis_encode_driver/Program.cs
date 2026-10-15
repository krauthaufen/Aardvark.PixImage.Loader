// Encodes images to KTX2 ETC1S / UASTC with Aardvark.PixImage.IO (for tests/oracle/gen_basis_encode.py).
//
//   basis_encode_driver out.ktx2 etc1s|uastc [options] image.png [image.png ...]
//     --q N          ETC1S quality 1..255 (default 128)
//     --effort E     0 fast, 1 normal (default), 2 high
//     --mips         computed mip chain
//     --array        the images are the layers of an array
//     --cube         the six images are cube faces (+X, -X, +Y, -Y, +Z, -Z)
//     --nozstd       UASTC without supercompression
//     --linear       linear transfer function (default sRGB)
using Aardvark.ImageCodecs;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: basis_encode_driver out.ktx2 etc1s|uastc [--q N] [--effort E] [--mips] [--array] [--cube] [--nozstd] [--linear] images...");
    return 2;
}
var o = new KtxEncodeOptions
{
    Compression = args[1] == "uastc" ? TextureCompression.Uastc : TextureCompression.Etc1s,
    Srgb = true,
};
bool array = false, cube = false;
var images = new List<RawImage>();
for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--q": o.Etc1sQuality = int.Parse(args[++i]); break;
        case "--effort": o.Quality = (BcQuality)int.Parse(args[++i]); break;
        case "--mips": o.GenerateMipmaps = true; break;
        case "--array": array = true; break;
        case "--cube": cube = true; break;
        case "--nozstd": o.Supercompression = KtxSupercompression.None; break;
        case "--linear": o.Srgb = false; break;
        default: images.Add(ImageCodec.Decode(args[i])); break;
    }
}
byte[] k;
if (cube) k = Ktx.EncodeCube(images.Select(im => (IReadOnlyList<RawImage>)new[] { im }).ToList(), o);
else if (array) k = Ktx.EncodeArray(images.Select(im => (IReadOnlyList<RawImage>)new[] { im }).ToList(), o);
else k = Ktx.Encode(images[0], o);
File.WriteAllBytes(args[0], k);
return 0;
