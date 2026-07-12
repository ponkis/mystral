using System.Windows.Media.Imaging;

namespace Mystral.Models;

public sealed record ArtworkAsset(byte[] Data, BitmapSource Preview);
