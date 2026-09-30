using System;
using System.IO;
using System.Linq;
using SvnMethodLens.Editor;
using SvnMethodLens.Core.Analysis;

var f = args[0];
var text = File.ReadAllText(f);
var light = EditorMethodScanner.Scan(text).OrderBy(m => m.StartLine).ToList();
var roslyn = MethodScanner.Scan(f, text).OrderBy(m => m.StartLine).ToList();
Console.WriteLine($"light={light.Count}  roslyn={roslyn.Count}");
var roslynStarts = roslyn.Select(m => m.StartLine).ToHashSet();
var missed = roslynStarts.Except(light.Select(m => m.StartLine)).OrderBy(x => x).ToList();
Console.WriteLine($"roslyn 有而 light 没有的行 ({missed.Count}): {string.Join(",", missed.Take(30))}");
foreach (var ln in missed.Take(10))
    Console.WriteLine($"  line {ln}: {text.Replace("\r\n","\n").Split('\n')[ln-1].Trim()}".Substring(0, Math.Min(110, $"  line {ln}: {text.Replace("\r\n","\n").Split('\n')[ln-1].Trim()}".Length)));
var extra = light.Select(m => m.StartLine).Except(roslynStarts).OrderBy(x => x).ToList();
Console.WriteLine($"light 有而 roslyn 没有的行 ({extra.Count}): {string.Join(",", extra.Take(20))}");
