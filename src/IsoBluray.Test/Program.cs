using System;
using System.Linq;
using Emby.IsoBluray.Bluray;
using Emby.IsoBluray.Udf;

// Usage: IsoBluray.Test <image.iso> [preferred language, ISO 639-2, default fra]
var iso = args[0];
Console.WriteLine("UDF? " + UdfImage.LooksLikeUdf(iso));
using var image = new UdfImage(iso);
Console.WriteLine("BDMV: " + string.Join(" ", image.ListDirectory(image.Resolve("/BDMV")).Select(e => e.Name)));
var disc = DiscInfo.Examine(image, args.Length > 1 ? args[1] : "fra");
var pl = disc.MainPlaylist;
Console.WriteLine($"Playlist {pl.Name} {pl.Duration} clips {string.Join(",", pl.Clips)}");
foreach (var c in disc.ClipFiles) Console.WriteLine($"  {c.Clip}.m2ts {c.Size} bytes, offset {c.Extents[0].Offset}, {c.Extents.Count} extents");
foreach (var s in pl.Streams) Console.WriteLine($"  {s.Kind,-8} 0x{s.Pid:x4} {s.Codec,-10} {s.Language}");
