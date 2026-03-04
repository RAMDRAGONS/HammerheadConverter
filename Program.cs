using System.IO;
using ShaderLibrary;
using BfresLibrary;
using LayoutLibrary;
using LayoutLibrary.Cafe;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using WoomLink;
using WoomLink.Ex;
using WoomLink.Ex.sead;
using WoomLink.sead;
using WoomLink.xlink2;
using WoomLink.xlink2.File;
using WoomLink.xlink2.Properties;

namespace HammerheadConverter;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help"))
        {
            PrintUsage();
            return 0;
        }

         if (args.Contains("--szs-extract"))
        {
            var rest = args.Where(a => a != "--szs-extract").ToArray();
            if (rest.Length < 2) { Console.Error.WriteLine("Usage: --szs-extract input.szs output_dir"); return 1; }
            byte[] dec = Oead.Yaz0DecompressFile(rest[0]);
            var files = Oead.SarcRead(dec);
            Directory.CreateDirectory(rest[1]);
            foreach (var f in files) { File.WriteAllBytes(Path.Combine(rest[1], f.Key), f.Value); Console.WriteLine($"  {f.Key} ({f.Value.Length} bytes)"); }
            return 0;
        }

        if (args.Contains("--layout-add-parts"))
            return RunLayoutAddParts(args.Where(a => a != "--layout-add-parts").ToArray());

        if (args.Contains("--decompile-paint"))
            return RunDecompilePaint(args.Where(a => a != "--decompile-paint").ToArray());

        if (args.Contains("--paint-diag"))
            return RunPaintDiag(args.Where(a => a != "--paint-diag").ToArray());

        if (args.Contains("--compare-kcl"))
        {
            var rest = args.Where(a => a != "--compare-kcl").ToArray();
            if (rest.Length < 2) { Console.Error.WriteLine("Usage: --compare-kcl testfire.szs prod.szs"); return 1; }
            Console.WriteLine("=== KCL Comparison ===");
            foreach (var (label, path) in new[] { ("Testfire", rest[0]), ("Prod", rest[1]) })
            {
                Console.WriteLine($"\n--- {label}: {Path.GetFileName(path)} ---");
                byte[] dec = Oead.Yaz0DecompressFile(path);
                var files = Oead.SarcRead(dec);
                foreach (var f in files.OrderBy(x => x.Key))
                {
                    string ext = Path.GetExtension(f.Key).ToLowerInvariant();
                    Console.WriteLine($"  {f.Key} ({f.Value.Length} bytes)");
                    if (ext == ".kcl" && f.Value.Length >= 64)
                    {
                        Console.Write("    Header[0..63]: ");
                        for (int i = 0; i < 64; i++) Console.Write($"{f.Value[i]:X2} ");
                        Console.WriteLine();
                        // KCL header offsets
                        uint off0 = BitConverter.ToUInt32(f.Value, 0);
                        uint off1 = BitConverter.ToUInt32(f.Value, 4);
                        uint off2 = BitConverter.ToUInt32(f.Value, 8);
                        uint off3 = BitConverter.ToUInt32(f.Value, 12);
                        Console.WriteLine($"    Offsets: 0x{off0:X} 0x{off1:X} 0x{off2:X} 0x{off3:X}");
                        // Check for V2 header (starts with 02020000)
                        if (f.Value[0] == 0x02 && f.Value[1] == 0x02)
                            Console.WriteLine("    Version: V2 (has version header)");
                        else
                            Console.WriteLine("    Version: V1 (no version header, starts with offsets)");
                    }
                }
            }
            return 0;
        }

        if (args.Contains("--roundtrip-test"))
        {
            var rest = args.Where(a => a != "--roundtrip-test").ToArray();
            if (rest.Length < 1) { Console.Error.WriteLine("Usage: --roundtrip-test input.szs"); return 1; }
            return RunRoundTripTest(rest[0]);
        }

        if (args.Contains("--bars-build"))
            return RunBarsBuild(args.Where(a => a != "--bars-build").ToArray());

        if (args.Contains("--szs-passthrough"))
        {
            // Pure SARC round-trip: decompress → read SARC → write SARC → compress
            // NO file modification at all. Tests if oead SARC/Yaz0 is byte-safe.
            var rest = args.Where(a => a != "--szs-passthrough").ToArray();
            if (rest.Length < 2) { Console.Error.WriteLine("Usage: --szs-passthrough input.szs output.szs"); return 1; }
            Console.WriteLine($"Passthrough: {Path.GetFileName(rest[0])}");
            byte[] dec = Oead.Yaz0DecompressFile(rest[0]);
            var files = Oead.SarcRead(dec);
            foreach (var f in files) Console.WriteLine($"  {f.Key}: {f.Value.Length} bytes");
            byte[] sarc = Oead.SarcWrite(files);
            Console.WriteLine($"  SARC: orig={dec.Length}, new={sarc.Length}");
            byte[] compressed = Oead.Yaz0Compress(sarc, dataAlignment: 0x2000);
            File.WriteAllBytes(rest[1], compressed);
            Console.WriteLine($"  ✓ Saved: {rest[1]} ({compressed.Length} bytes)");
            return 0;
        }

        if (args.Contains("--paint-diag"))
        {
            // Dump paint-related shader options from two bfres files
            var rest = args.Where(a => a != "--paint-diag").ToArray();
            foreach (var bfresPath in rest)
            {
                var bfres = new ResFile(new MemoryStream(File.ReadAllBytes(bfresPath)));
                Console.WriteLine($"\n=== {Path.GetFileName(bfresPath)} (V{bfres.VersionMajor2}) ===");
                foreach (var model in bfres.Models.Values)
                {
                    Console.WriteLine($"  Model: {model.Name} ({model.Materials.Count} materials)");
                    foreach (var mat in model.Materials.Values)
                    {
                        var sa = mat.ShaderAssign;
                        string paintType = "N/A", objPaintType = "N/A";
                        if (sa?.ShaderOptions != null)
                        {
                            if (sa.ShaderOptions.TryGetValue("blitz_paint_type", out var pt)) paintType = pt;
                            if (sa.ShaderOptions.TryGetValue("blitz_obj_paint_type", out var opt)) objPaintType = opt;
                        }
                        if ((paintType != null && paintType != "N/A") || (objPaintType != null && objPaintType != "N/A"))
                        {
                            Console.WriteLine($"    {mat.Name}: shader={sa?.ShadingModelName} paint_type={paintType ?? "null"} obj_paint_type={objPaintType ?? "null"}");
                        }
                    }
                }
            }
            return 0;
        }

        if (args.Contains("--option-diff"))
        {
            // Compare ALL shader options between testfire and prod bfres for matching materials
            var rest = args.Where(a => a != "--option-diff").ToArray();
            if (rest.Length < 2) { Console.Error.WriteLine("Usage: --option-diff tf.bfres prod.bfres [materialName]"); return 1; }
            var tf = new ResFile(new MemoryStream(File.ReadAllBytes(rest[0])));
            var prod = new ResFile(new MemoryStream(File.ReadAllBytes(rest[1])));
            string filterMat = rest.Length > 2 ? rest[2] : null;

            // Build map of testfire material options
            var tfMats = new Dictionary<string, Dictionary<string, string>>();
            foreach (var model in tf.Models.Values)
                foreach (var mat in model.Materials.Values)
                    if (mat.ShaderAssign?.ShaderOptions != null)
                    {
                        var opts = new Dictionary<string, string>();
                        foreach (var o in mat.ShaderAssign.ShaderOptions) opts[o.Key] = o.Value;
                        tfMats[mat.Name] = opts;
                    }

            foreach (var model in prod.Models.Values)
                foreach (var mat in model.Materials.Values)
                {
                    if (filterMat != null && mat.Name != filterMat) continue;
                    if (mat.ShaderAssign?.ShaderOptions == null) continue;
                    var prodOpts = new Dictionary<string, string>();
                    foreach (var o in mat.ShaderAssign.ShaderOptions) prodOpts[o.Key] = o.Value;

                    if (!tfMats.TryGetValue(mat.Name, out var tfOpts))
                    {
                        Console.WriteLine($"\n{mat.Name}: prod-only (no testfire match), {prodOpts.Count} options");
                        continue;
                    }

                    // Find differences
                    var diffs = new List<string>();
                    var prodOnly = new List<string>();
                    var tfOnly = new List<string>();

                    foreach (var kvp in prodOpts)
                    {
                        if (tfOpts.TryGetValue(kvp.Key, out var tfVal))
                        {
                            if (kvp.Value != tfVal) diffs.Add($"  {kvp.Key}: TF={tfVal} → PROD={kvp.Value}");
                        }
                        else prodOnly.Add($"  +{kvp.Key}={kvp.Value}");
                    }
                    foreach (var kvp in tfOpts)
                        if (!prodOpts.ContainsKey(kvp.Key)) tfOnly.Add($"  -{kvp.Key}={kvp.Value}");

                    if (diffs.Count > 0 || prodOnly.Count > 0 || tfOnly.Count > 0)
                    {
                        Console.WriteLine($"\n{mat.Name}: {diffs.Count} changed, {prodOnly.Count} prod-only, {tfOnly.Count} tf-only");
                        foreach (var d in diffs) Console.WriteLine(d);
                        foreach (var d in prodOnly) Console.WriteLine(d);
                        foreach (var d in tfOnly) Console.WriteLine(d);
                    }
                    else Console.WriteLine($"\n{mat.Name}: IDENTICAL ({prodOpts.Count} options)");
                }
            return 0;
        }

        if (args.Contains("--raw-patch-szs"))
            return RunRawPatchMode(args.Where(a => a != "--raw-patch-szs").ToArray());

        if (args.Contains("--szs-diag"))
            return RunSzsDiag(args.Where(a => a != "--szs-diag").ToArray());

        if (args.Contains("--compare-shaders"))
        {
            // Compare prod vs testfire shader models from shared SZS files
            string prodDir = null, testfireDir = null, compareOutputDir = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--prod-dir") prodDir = args[++i];
                else if (args[i] == "--testfire-dir") testfireDir = args[++i];
                else if (args[i] == "--output-dir") compareOutputDir = args[++i];
            }
            prodDir ??= "100model";
            testfireDir ??= "shadict";
            compareOutputDir ??= "/tmp/shader_compare";

            string[] sharedFiles = { "Fld_Ditch01.Nin_NX_NVN.szs", "Obj_AirBall.Nin_NX_NVN.szs", "Player00.Nin_NX_NVN.szs" };
            foreach (var f in sharedFiles)
            {
                string prodPath = Path.Combine(prodDir, f);
                string tfPath = Path.Combine(testfireDir, f);
                if (!File.Exists(prodPath)) { Console.WriteLine($"Skip {f}: not found in prod dir"); continue; }
                if (!File.Exists(tfPath)) { Console.WriteLine($"Skip {f}: not found in testfire dir"); continue; }
                ShaderComparer.CompareSharedFile(prodPath, tfPath);
                ShaderComparer.DecompileAndCompare(prodPath, tfPath, compareOutputDir);
            }
            return 0;
        }

        if (args.Contains("--dump-material"))
        {
            // Usage: --dump-material file1.szs file2.szs [materialName]
            var rest = args.Where(a => !a.StartsWith("--")).ToArray();
            if (rest.Length < 2) { Console.Error.WriteLine("Usage: --dump-material file1.szs file2.szs [materialName]"); return 1; }
            string filterMat = rest.Length > 2 ? rest[2] : null;

            var files = new[] { rest[0], rest[1] };
            var allOpts = new Dictionary<string, Dictionary<string, string>>[2];

            for (int fi = 0; fi < 2; fi++)
            {
                allOpts[fi] = new Dictionary<string, Dictionary<string, string>>();
                byte[] dec = Oead.Yaz0DecompressFile(files[fi]);
                var sarc = Oead.SarcRead(dec);
                foreach (var entry in sarc.Where(e => Path.GetExtension(e.Key).Equals(".bfres", StringComparison.OrdinalIgnoreCase)))
                {
                    var bfres = new ResFile(new MemoryStream(entry.Value));
                    foreach (var model in bfres.Models.Values)
                        foreach (var mat in model.Materials.Values)
                        {
                            if (filterMat != null && !mat.Name.Contains(filterMat, StringComparison.OrdinalIgnoreCase)) continue;
                            var opts = new Dictionary<string, string>();
                            if (mat.ShaderAssign?.ShaderOptions != null)
                                foreach (var o in mat.ShaderAssign.ShaderOptions) opts[o.Key] = o.Value;
                            allOpts[fi][mat.Name] = opts;
                        }
                }
            }

            var allNames = allOpts[0].Keys.Union(allOpts[1].Keys).OrderBy(n => n);
            foreach (var name in allNames)
            {
                var has0 = allOpts[0].TryGetValue(name, out var opts0);
                var has1 = allOpts[1].TryGetValue(name, out var opts1);
                Console.WriteLine($"\n=== {name} ===");
                if (!has0) { Console.WriteLine("  [MISSING in file1]"); continue; }
                if (!has1) { Console.WriteLine("  [MISSING in file2]"); continue; }

                var allKeys = opts0.Keys.Union(opts1.Keys).OrderBy(k => k);
                int diffs = 0;
                foreach (var key in allKeys)
                {
                    var v0 = opts0.TryGetValue(key, out var val0) ? val0 : "(absent)";
                    var v1 = opts1.TryGetValue(key, out var val1) ? val1 : "(absent)";
                    if (v0 != v1) { Console.WriteLine($"  DIFF {key}: {v0} → {v1}"); diffs++; }
                }
                if (diffs == 0) Console.WriteLine("  (identical)");
            }
            return 0;
        }

        if (args.Contains("--mat-diag"))
        {
            string szsPath = args.FirstOrDefault(a => a.EndsWith(".szs") && !a.StartsWith("--")) ?? "100model/Fld_Ditch01.Nin_NX_NVN.szs";
            MaterialDiagnostic.Run(szsPath, new[] {
                "prod_Blitz_Proc.Nin_NX_NVN.release.szs",
                "testfire_Blitz_Proc.Nin_NX_NVN.release.szs",
                "prod_gsys_resource.Nin_NX_NVN.release.szs",
                "testfire_gsys_resource.Nin_NX_NVN.release.szs"
            });
            return 0;
        }

        if (args.Contains("--diag"))
            return RunDiagnostic(args);

        string prodBfshaPath = null;
        string prodBfresPath = null;
        string testfireBfresPath = null;
        string testfireBfshaPath = null;
        string shadictPath = null;
        string outputDir = null;
        string batchSzsDir = null;
        string refSzsPath = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--prod-bfsha":
                    prodBfshaPath = args[++i];
                    break;
                case "--prod-bfres":
                    prodBfresPath = args[++i];
                    break;
                case "--testfire-bfres":
                    testfireBfresPath = args[++i];
                    break;
                case "--testfire-bfsha":
                    testfireBfshaPath = args[++i];
                    break;
                case "--shadict":
                    shadictPath = args[++i];
                    break;
                case "--output-dir":
                    outputDir = args[++i];
                    break;
                case "--batch-szs":
                    batchSzsDir = args[++i];
                    break;
                case "--ref-szs":
                    refSzsPath = args[++i];
                    break;
            }
        }

        // Validate inputs
        var errors = new List<string>();
        if (outputDir == null) errors.Add("--output-dir is required");

        // Detect mode
        bool batchMode = batchSzsDir != null;
        bool hybridMode = shadictPath != null && !batchMode;

        if (batchMode)
        {
            if (refSzsPath == null) errors.Add("--ref-szs is required in batch mode");
        }
        else if (hybridMode)
        {
            if (prodBfresPath == null) errors.Add("--prod-bfres is required in hybrid mode");
            if (testfireBfresPath == null) errors.Add("--testfire-bfres is required in hybrid mode");
            if (testfireBfshaPath == null) errors.Add("--testfire-bfsha is required in hybrid mode");
        }
        else
        {
            if (prodBfshaPath == null) errors.Add("--prod-bfsha is required (or use --shadict for hybrid mode)");
        }

        if (errors.Count > 0)
        {
            foreach (var err in errors)
                Console.Error.WriteLine($"ERROR: {err}");
            PrintUsage();
            return 1;
        }

        // Ensure output directory exists
        Directory.CreateDirectory(outputDir);

        try
        {
            if (batchMode)
                return RunBatchSzsMode(batchSzsDir, refSzsPath, shadictPath, outputDir);
            else if (hybridMode)
                return RunHybridMode(prodBfresPath, testfireBfresPath, testfireBfshaPath, shadictPath, outputDir);
            else
                return RunDowngradeMode(prodBfshaPath, prodBfresPath, testfireBfresPath, outputDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nERROR: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    /// <summary>
    /// Hybrid mode: Use testfire bfsha as-is, adapt prod bfres materials to match
    /// available testfire shader programs found in shadict SZS archives.
    /// </summary>
    static int RunHybridMode(string prodBfresPath, string testfireBfresPath,
         string testfireBfshaPath, string shadictPath, string outputDir)
    {
        Console.WriteLine("\n=== HYBRID MODE ===");
        Console.WriteLine("Strategy: Cherry-pick programs from all testfire bfsha + adapt remaining\n");

        // Load prod bfres
        Console.WriteLine($"Loading prod bfres: {prodBfresPath}");
        var prodBfres = new ResFile(prodBfresPath);

        // Load testfire bfres template
        Console.WriteLine($"Loading testfire bfres: {testfireBfresPath}");
        var testfireBfres = new ResFile(testfireBfresPath);

        // Load the testfire bfsha (this will be modified in-place with cherry-picked programs)
        Console.WriteLine($"Loading testfire bfsha: {testfireBfshaPath}");
        var targetBfsha = new BfshaFile(testfireBfshaPath);

        // Scan shadict for all testfire shader models AND material options
        Console.WriteLine($"\nScanning shadict: {shadictPath}");
        var scanResult = BfshaHybridBuilder.ScanShadictAll(shadictPath);
        var allModels = scanResult.ShaderModels;
        var globalKnownGoodMats = scanResult.KnownGoodMaterials;

        // Build material option sets from prod bfres
        var materialOptionSets = new List<(string MatName, string ShaderModelName, Dictionary<string, string> Options)>();
        foreach (var model in prodBfres.Models.Values)
        {
            foreach (var mat in model.Materials.Values)
            {
                var shaderName = mat.ShaderAssign?.ShadingModelName;
                if (shaderName == null) continue;

                var matOpts = new Dictionary<string, string>();
                if (mat.ShaderAssign?.ShaderOptions != null)
                    foreach (var pair in mat.ShaderAssign.ShaderOptions)
                        matOpts[pair.Key] = pair.Value;

                materialOptionSets.Add((mat.Name, shaderName, matOpts));
            }
        }

        // Cherry-pick programs into target bfsha
        Console.WriteLine("\n=== Cherry-picking programs ===");
        var assemblyResult = BfshaHybridBuilder.AssembleHybridBfsha(
            targetBfsha, allModels, materialOptionSets);

        Console.WriteLine($"\n=== Assembly summary: {assemblyResult.PreExistingCount} pre-existing, " +
            $"{assemblyResult.CherryPickedCount} cherry-picked, " +
            $"{assemblyResult.FallbackMaterials.Count} need fallback ===");

        // For fallback materials, run the old matching logic
        var matchResults = new List<BfshaHybridBuilder.MatchResult>();
        if (assemblyResult.FallbackMaterials.Count > 0)
        {
            Console.WriteLine("\n=== Matching fallback materials ===");
            var fallbackSet = new HashSet<string>(assemblyResult.FallbackMaterials);

            foreach (var matSet in materialOptionSets)
            {
                if (!fallbackSet.Contains(matSet.MatName)) continue;

                if (allModels.ContainsKey(matSet.ShaderModelName))
                {
                    var match = BfshaHybridBuilder.FindBestMatch(
                        matSet.MatName, matSet.ShaderModelName, matSet.Options,
                        allModels[matSet.ShaderModelName]);
                    matchResults.Add(match);
                }
                else
                {
                    Console.WriteLine($"  ✗ '{matSet.MatName}' ({matSet.ShaderModelName}): no testfire shader model found");
                }
            }
        }

        // Convert bfres (prod → testfire structure) — this mutates testfireBfres
        var convertedBfres = BfresConverter.Convert(prodBfres, testfireBfres);

        // Adapt materials: known-good for covered materials, fallback for the rest
        BfresConverter.AdaptMaterialsForTestfire(convertedBfres, globalKnownGoodMats, matchResults);

        // Save adapted bfres
        string outputBfresPath = Path.Combine(outputDir, Path.GetFileName(prodBfresPath));
        // CRITICAL: Reset static BufferOffset right before Save. Any ResFile load
        // between Convert() and here will have overwritten this static field.
        BufferInfo.BufferOffset = 0;
        convertedBfres.Save(outputBfresPath);
        Console.WriteLine($"\nSaved adapted bfres: {outputBfresPath}");

        // Save the modified bfsha (now contains cherry-picked programs)
        string outputBfshaPath = Path.Combine(outputDir, Path.GetFileName(testfireBfshaPath));
        targetBfsha.Save(outputBfshaPath);
        Console.WriteLine($"Saved hybrid bfsha: {outputBfshaPath}");

        // Verify outputs
        Console.WriteLine("\nVerifying outputs...");
        var reloadedBfres = new ResFile(outputBfresPath);
        Console.WriteLine($"  ✓ Bfres OK: {reloadedBfres.Models.Count} models, " +
            $"Major2={reloadedBfres.VersionMajor2}");

        var reloadedBfsha = new BfshaFile(outputBfshaPath);
        for (int i = 0; i < reloadedBfsha.ShaderModels.Count; i++)
        {
            var sm = reloadedBfsha.ShaderModels.Values.ElementAt(i);
            Console.WriteLine($"  ✓ Bfsha '{sm.Name}': {sm.Programs.Count} programs");
        }

        Console.WriteLine("\n=== Hybrid conversion complete ===");
        return 0;
    }

    /// <summary>
    /// Original downgrade mode: V7→V5 bfsha downgrade + optional bfres conversion.
    /// </summary>
    static int RunDowngradeMode(string prodBfshaPath, string prodBfresPath,
        string testfireBfresPath, string outputDir)
    {
        // === BFSHA Conversion (V7→V5 Downgrade) ===
        Console.WriteLine($"Loading prod bfsha: {prodBfshaPath}");
        var prodBfsha = new BfshaFile(prodBfshaPath);

        var convertedBfsha = BfshaConverter.Downgrade(prodBfsha);

        string outputBfshaPath = Path.Combine(outputDir, Path.GetFileName(prodBfshaPath));
        convertedBfsha.Save(outputBfshaPath);
        Console.WriteLine($"\nSaved downgraded bfsha: {outputBfshaPath}");

        // Verify: reload and check
        Console.WriteLine("Verifying bfsha...");
        var reloaded = new BfshaFile(outputBfshaPath);
        Console.WriteLine($"  ✓ Reloaded OK: {reloaded.ShaderModels.Count} shader models, " +
            $"version Major={reloaded.BinHeader.VersionMajor}");

        // === BFRES Conversion (optional) ===
        if (prodBfresPath != null && testfireBfresPath != null)
        {
            Console.WriteLine($"\nLoading prod bfres: {prodBfresPath}");
            var prodBfres = new ResFile(prodBfresPath);
            Console.WriteLine($"Loading testfire bfres: {testfireBfresPath}");
            var testfireBfres = new ResFile(testfireBfresPath);

            var convertedBfres = BfresConverter.Convert(prodBfres, testfireBfres);

            string outputBfresPath = Path.Combine(outputDir, Path.GetFileName(prodBfresPath));
            // CRITICAL: Reset static BufferOffset right before Save.
            BufferInfo.BufferOffset = 0;
            convertedBfres.Save(outputBfresPath);
            Console.WriteLine($"\nSaved converted bfres: {outputBfresPath}");

            // Verify: reload and check
            Console.WriteLine("Verifying bfres...");
            var reloadedBfres = new ResFile(outputBfresPath);
            Console.WriteLine($"  ✓ Reloaded OK: {reloadedBfres.Models.Count} models, " +
                $"Major2={reloadedBfres.VersionMajor2}");
        }
        else if (prodBfresPath != null)
        {
            Console.WriteLine("\nWARNING: --testfire-bfres also required for bfres conversion. Skipping.");
        }

        Console.WriteLine("\n=== Conversion complete ===");
        return 0;
    }

    static int RunDiagnostic(string[] args)
    {
        Console.WriteLine("=== DIAGNOSTIC MODE ===");
        Console.WriteLine($"BinaryHeader Unsafe.SizeOf = {System.Runtime.CompilerServices.Unsafe.SizeOf<BinaryHeader>()}");
        Console.WriteLine($"BinaryHeader Marshal.SizeOf = {System.Runtime.InteropServices.Marshal.SizeOf<BinaryHeader>()}");

        // Find bfsha paths from args
        var paths = args.Where(a => a.EndsWith(".bfsha") && File.Exists(a)).ToList();
        if (paths.Count == 0)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if ((args[i] == "--prod-bfsha" || args[i] == "--testfire-bfsha") && File.Exists(args[i+1]))
                    paths.Add(args[i+1]);
            }
        }

        foreach (var path in paths)
        {
            Console.WriteLine($"\n--- Diagnosing: {path} ---");
            Console.WriteLine($"  File size: {new FileInfo(path).Length} bytes");

            using (var fs = File.OpenRead(path))
            {
                var br = new BinaryReader(fs);
                var headerBytes = br.ReadBytes(64);
                Console.WriteLine("  First 64 bytes:");
                for (int r = 0; r < 4; r++)
                {
                    var hex = string.Join(" ", headerBytes.Skip(r*16).Take(16).Select(b => $"{b:X2}"));
                    Console.WriteLine($"    0x{r*16:X2}: {hex}");
                }

                fs.Position = 0;
                ulong magic = br.ReadUInt64();
                byte vMicro = br.ReadByte();
                byte vMinor = br.ReadByte();
                ushort vMajor = br.ReadUInt16();
                ushort byteOrder = br.ReadUInt16();
                byte alignment = br.ReadByte();
                byte targetAddr = br.ReadByte();
                uint nameOffset = br.ReadUInt32();
                ushort flag = br.ReadUInt16();
                ushort blockOffset = br.ReadUInt16();
                uint relocTableOffset = br.ReadUInt32();
                uint fileSize = br.ReadUInt32();

                Console.WriteLine($"  Version: Major={vMajor}, Minor={vMinor}, Micro={vMicro}");
                Console.WriteLine($"  ByteOrder=0x{byteOrder:X4}, Alignment={alignment}, TargetAddr={targetAddr}");
                Console.WriteLine($"  NameOffset=0x{nameOffset:X8}, Flag=0x{flag:X4}, BlockOffset=0x{blockOffset:X4}");
                Console.WriteLine($"  RelocTableOffset=0x{relocTableOffset:X8}, FileSize=0x{fileSize:X8} ({fileSize})");
                Console.WriteLine($"  Position after BinHeader: {fs.Position}");
            }

            try
            {
                var bfsha = new BfshaFile(path);
                Console.WriteLine($"  ✓ Loaded OK. VersionMajor={bfsha.BinHeader.VersionMajor}");
                Console.WriteLine($"    ShaderModels: {bfsha.ShaderModels.Count}");
                for (int i = 0; i < bfsha.ShaderModels.Count; i++)
                    Console.WriteLine($"      [{i}] {bfsha.ShaderModels[i].Name}: {bfsha.ShaderModels[i].Programs.Count} programs");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Load FAILED: {ex.Message}");
                var lines = ex.StackTrace?.Split('\n') ?? Array.Empty<string>();
                foreach (var line in lines.Take(5))
                    Console.WriteLine($"    {line.Trim()}");
            }
        }
        return 0;
    }

    /// <summary>
    /// Returns true if a material name indicates a minimap material (Map* or Drcmap*).
    /// These use screen-space UV projection and must not be mixed with field materials.
    /// </summary>
    static bool IsMapMaterial(string name) =>
        name.StartsWith("Map", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Drcmap", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a set of program indices in the shader model that correspond to Map* (minimap)
    /// known-good materials. Used to exclude these programs when matching non-Map materials.
    /// </summary>
    static HashSet<int> BuildMapProgramIndices(
        ShaderModel sm,
        Dictionary<string, Dictionary<string, string>> knownGoodPool)
    {
        var mapIndices = new HashSet<int>();
        foreach (var kv in knownGoodPool)
        {
            if (!IsMapMaterial(kv.Key)) continue;
            var sanitized = BfshaHybridBuilder.SanitizeOptions(kv.Value, sm);
            try
            {
                int idx = sm.GetProgramIndex(sanitized);
                if (idx >= 0) mapIndices.Add(idx);
            }
            catch { }
        }
        return mapIndices;
    }

    /// <summary>
    /// Batch SZS mode: convert all SZS files in a directory.
    /// Uses a reference testfire SZS for bfres version template.
    /// The prod bfsha is downgraded V7→V5 to keep all original shader programs.
    /// </summary>
    static int RunBatchSzsMode(string inputDir, string refSzsPath, string shadictPath, string outputDir)
    {
        Console.WriteLine("\n=== BATCH SZS MODE ===");
        Console.WriteLine($"Input directory:  {inputDir}");
        Console.WriteLine($"Reference SZS:    {refSzsPath}");
        Console.WriteLine($"Output directory:  {outputDir}\n");

        // Load reference SZS once — we only need the bfres for version template
        Console.WriteLine("Loading reference SZS...");
        byte[] refDecompressed = Oead.Yaz0DecompressFile(refSzsPath);
        var refSarcFiles = Oead.SarcRead(refDecompressed);

         // Extract ref bfres (used as version template for bfres downgrade)
        var refBfresEntry = refSarcFiles.FirstOrDefault(f => Path.GetExtension(f.Key).Equals(".bfres", StringComparison.OrdinalIgnoreCase));
        var refBfshaEntry = refSarcFiles.FirstOrDefault(f => Path.GetExtension(f.Key).Equals(".bfsha", StringComparison.OrdinalIgnoreCase));

        if (refBfresEntry.Value == null)
        {
            Console.Error.WriteLine("ERROR: Reference SZS contains no .bfres file");
            return 1;
        }

        Console.WriteLine($"  Reference bfres: {refBfresEntry.Key}");

        // Extract testfire uniform block sizes from ref bfsha.
        // The testfire game expects these exact sizes for GPU buffer allocation.
        var testfireUBSizes = new Dictionary<string, uint>();
        int testfireMaterialBlockSize = 912; // default fallback
        BfshaFile refBfsha = null;
        if (refBfshaEntry.Value != null)
        {
            Console.WriteLine($"  Reference bfsha: {refBfshaEntry.Key}");
            refBfsha = new BfshaFile(new MemoryStream(refBfshaEntry.Value));
            // Use first shader model's UB sizes as reference
            if (refBfsha.ShaderModels.Count > 0)
            {
                var refSm = refBfsha.ShaderModels.Values.First();
                foreach (var ub in refSm.UniformBlocks)
                {
                    testfireUBSizes[ub.Key] = ub.Value.Size;
                    if (ub.Key == "gsys_material")
                        testfireMaterialBlockSize = (int)ub.Value.Size;
                }
                Console.WriteLine($"    Testfire gsys_material size: {testfireMaterialBlockSize}");
                Console.WriteLine($"    Testfire UB count: {testfireUBSizes.Count}");
            }
        }
        else
        {
            Console.WriteLine("  WARNING: No ref bfsha found, using default material block size 912");
        }

        // Scan ALL testfire Obj SZS files for Obj shader models AND known-good materials.
        // Different Obj files may have different shader models (e.g., one has blitz_uber_obj,
        // another may have blitz_uber_obj_np). We merge all unique models.
        BfshaFile objRefBfsha = null;
        var objKnownGoodMaterials = new Dictionary<string, (string ShaderModel, Dictionary<string, string> Options)>();
        string shadictDir2 = Path.GetDirectoryName(refSzsPath)!;
        var objSzsFiles = Directory.GetFiles(shadictDir2, "Obj_*.szs");
        if (objSzsFiles.Length > 0)
        {
            Console.WriteLine($"\n  Scanning {objSzsFiles.Length} Obj SZS files for shader models & materials...");
            objRefBfsha = new BfshaFile();
            objRefBfsha.BinHeader = refBfsha?.BinHeader ?? new BinaryHeader();
            objRefBfsha.Name = "ObjComposite";
            var seenModels = new HashSet<string>();

            foreach (var objFile in objSzsFiles.OrderBy(f => f))
            {
                try
                {
                    byte[] objDecompressed = Oead.Yaz0DecompressFile(objFile);
                    var objSarcFiles = Oead.SarcRead(objDecompressed);

                    // Collect shader models from bfsha
                    var objBfshaEntry = objSarcFiles.FirstOrDefault(f => Path.GetExtension(f.Key).Equals(".bfsha", StringComparison.OrdinalIgnoreCase));
                    if (objBfshaEntry.Value != null)
                    {
                        var objBfsha = new BfshaFile(new MemoryStream(objBfshaEntry.Value));
                        foreach (var smEntry in objBfsha.ShaderModels)
                        {
                            if (!seenModels.Contains(smEntry.Value.Name))
                            {
                                objRefBfsha.ShaderModels.Add(smEntry.Key, smEntry.Value);
                                seenModels.Add(smEntry.Value.Name);
                                Console.WriteLine($"    Found model: {smEntry.Value.Name} ({smEntry.Value.Programs.Count} programs) in {Path.GetFileName(objFile)}");
                            }
                        }
                    }

                    // Collect known-good materials from bfres
                    var objBfresEntry = objSarcFiles.FirstOrDefault(f => Path.GetExtension(f.Key).Equals(".bfres", StringComparison.OrdinalIgnoreCase));
                    if (objBfresEntry.Value != null)
                    {
                        var objBfres = new ResFile(new MemoryStream(objBfresEntry.Value));
                        foreach (var model in objBfres.Models.Values)
                            foreach (var mat in model.Materials.Values)
                            {
                                var sa = mat.ShaderAssign;
                                if (sa?.ShadingModelName == null || sa.ShaderOptions == null) continue;
                                if (!objKnownGoodMaterials.ContainsKey(mat.Name))
                                {
                                    var opts = new Dictionary<string, string>();
                                    foreach (var opt in sa.ShaderOptions) opts[opt.Key] = opt.Value;
                                    objKnownGoodMaterials[mat.Name] = (sa.ShadingModelName, opts);
                                }
                            }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    WARN: Failed to read {Path.GetFileName(objFile)}: {ex.Message}");
                }
            }
            Console.WriteLine($"    Total Obj shader models: {objRefBfsha.ShaderModels.Count}");
            Console.WriteLine($"    Total Obj known-good materials: {objKnownGoodMaterials.Count}");
            // Show shader model distribution
            var smDist = objKnownGoodMaterials.GroupBy(kv => kv.Value.ShaderModel)
                .OrderBy(g => g.Key);
            foreach (var g in smDist)
                Console.WriteLine($"      {g.Key}: {g.Count()} materials");
        }
        else
            Console.WriteLine("  WARNING: No Obj_* SZS found in shadict for Obj reference");

        // Find all prod SZS files
        var szsFiles = Directory.GetFiles(inputDir, "*.szs").OrderBy(f => f).ToArray();
        Console.WriteLine($"\nFound {szsFiles.Length} SZS files to convert\n");

        int success = 0, failed = 0, skipped = 0;

        // Scan shadict for all testfire shader models (for program cherry-picking)
        string shadictDir = Path.GetDirectoryName(refSzsPath)!;
        Console.WriteLine($"\n  Scanning shadict: {shadictDir}");
        var scanResult = BfshaHybridBuilder.ScanShadictAll(shadictDir);
        int totalModels = scanResult.ShaderModels.Values.Sum(list => list.Count);
        Console.WriteLine($"    Found {totalModels} shader model instances across {scanResult.ShaderModels.Count} model names");

        foreach (var szsFile in szsFiles)
        {
            string szsName = Path.GetFileName(szsFile);
            Console.WriteLine($"\n{'='+ new string('=', 59)}");
            Console.WriteLine($"Processing: {szsName}");
            Console.WriteLine(new string('=', 60));

            try
            {
                // Decompress and read SARC
                byte[] decompressed = Oead.Yaz0DecompressFile(szsFile);
                var sarcFiles = Oead.SarcRead(decompressed);

                var outputFiles = new Dictionary<string, byte[]>();
                bool hasBfres = false, hasBfsha = false;

                // Detect asset type for logging
                bool isObjType = szsName.StartsWith("Obj_", StringComparison.OrdinalIgnoreCase);

                // First pass: collect all prod material option sets for hybrid assembly
                var materialOptionSets = new List<(string MatName, string ShaderModelName, Dictionary<string, string> Options)>();
                ResFile convertedBfres = null;
                var knownGoodSet = new HashSet<string>();

                foreach (var entry in sarcFiles)
                {
                    string ext = Path.GetExtension(entry.Key).ToLowerInvariant();

                    if (ext == ".bfres")
                    {
                        hasBfres = true;
                        Console.WriteLine($"  Converting bfres: {entry.Key}");

                        var prodBfres = new ResFile(new MemoryStream(entry.Value));
                        var refBfresLocal = new ResFile(new MemoryStream(refBfresEntry.Value));
                        convertedBfres = BfresConverter.Convert(prodBfres, refBfresLocal);

                        if (szsName.StartsWith("Fld_Deli_", StringComparison.OrdinalIgnoreCase))
                        {
                            HandleDeliTextures(convertedBfres, inputDir);
                        }

                        // NOTE: With the shader transplant approach, we use the prod bfsha
                        // (which contains programs compiled for prod option combinations).
                        // Materials keep their ORIGINAL prod options — no testfire option
                        // application needed. The prod-only options will be stripped later
                        // by StripProdOnlyOptions.
                        // (The old hybrid approach required testfire options because it used
                        // testfire bfsha programs.)

                        // Collect material option sets for hybrid bfsha assembly
                        foreach (var model in convertedBfres.Models.Values)
                            foreach (var mat in model.Materials.Values)
                            {
                                var sa = mat.ShaderAssign;
                                if (sa?.ShadingModelName == null || sa.ShaderOptions == null) continue;

                                var matOpts = new Dictionary<string, string>();
                                foreach (var opt in sa.ShaderOptions)
                                    matOpts[opt.Key] = opt.Value;

                                materialOptionSets.Add((mat.Name, sa.ShadingModelName, matOpts));
                            }
                    }
                    else if (ext == ".bfsha")
                    {
                        hasBfsha = true;
                    }
                    else
                    {
                        outputFiles[entry.Key] = entry.Value;
                    }
                }


                if (convertedBfres == null || !hasBfres)
                {
                    Console.WriteLine($"  ⚠ No bfres found in {szsName} — copying as-is");
                    File.Copy(szsFile, Path.Combine(outputDir, szsName), overwrite: true);
                    skipped++;
                    continue;
                }

                // ═══════════════════════════════════════════════════════════════════════
                // Shader Transplant Pipeline
                // Instead of remapping materials to testfire shader programs (which loses
                // original shader code and causes broken/inaccurate rendering), we:
                //   1. Load the prod bfsha from this SZS archive
                //   2. Downgrade V7→V5 format (BfshaConverter)
                //   3. Adapt metadata for testfire (ShaderTransplant)
                //   4. Strip prod-only options from material ShaderAssigns
                //   5. Fill missing options with shader model defaults
                // Materials keep their original GPU bytecode and options — no template needed.
                // ═══════════════════════════════════════════════════════════════════════

                // Load prod bfsha from the SZS
                var prodBfshaEntry = sarcFiles.FirstOrDefault(kv =>
                    Path.GetExtension(kv.Key).Equals(".bfsha", StringComparison.OrdinalIgnoreCase));

                BfshaFile hybridBfsha;
                bool forceTestfireBfsha = Environment.GetCommandLineArgs().Contains("--use-testfire-bfsha");
                bool isFieldAsset = szsName.StartsWith("Fld_", StringComparison.OrdinalIgnoreCase);
                bool useTestfireBfsha = forceTestfireBfsha || isFieldAsset;

                if (prodBfshaEntry.Key != null && !useTestfireBfsha)
                {
                    // Load and downgrade the prod bfsha
                    var prodBfsha = new BfshaFile(new MemoryStream(prodBfshaEntry.Value));
                    hybridBfsha = BfshaConverter.Downgrade(prodBfsha);

                    // Load testfire bfsha as reference for option structure
                    BfshaFile testfireRef = null;
                    try { testfireRef = new BfshaFile(new MemoryStream(refBfshaEntry.Value)); }
                    catch { }

                    // Adapt prod bfsha metadata for testfire
                    ShaderTransplant.AdaptForTestfire(hybridBfsha, testfireRef);
                }
                else if (useTestfireBfsha)
                {
                    // Use testfire bfsha for field assets (V7→V5 conversion breaks field paint).
                    // Materials whose shader model isn't in testfire bfsha will be remapped to
                    // the closest matching testfire program via hybrid template matching below.
                    Console.WriteLine(isFieldAsset
                        ? $"  *** USING TESTFIRE BFSHA for field asset '{szsName}' (fixes ground paint) ***"
                        : "  *** USING TESTFIRE BFSHA (manual override) ***");
                    hybridBfsha = new BfshaFile(new MemoryStream(refBfshaEntry.Value));
                }
                else
                {
                    // No prod bfsha (meshless asset) — use testfire bfsha as-is
                    Console.WriteLine("  No prod bfsha found — using testfire bfsha as fallback");
                    hybridBfsha = new BfshaFile(new MemoryStream(refBfshaEntry.Value));
                }

                // Adapt material options to match the bfsha being used.
                int strippedCount = 0;
                if (useTestfireBfsha)
                {
                    // For field assets: use testfire options for materials using native testfire
                    // shader models. Materials whose shader model isn't in testfire bfsha get
                    // remapped to the closest matching testfire program (hybrid template approach).
                    var refBfresLocal2 = new ResFile(new MemoryStream(refBfresEntry.Value));
                    var tfKnownGood = new Dictionary<string, Dictionary<string, string>>();
                    foreach (var m in refBfresLocal2.Models.Values)
                        foreach (var mat in m.Materials.Values)
                        {
                            if (mat.ShaderAssign?.ShaderOptions == null) continue;
                            var opts = new Dictionary<string, string>();
                            foreach (var o in mat.ShaderAssign.ShaderOptions)
                                opts[o.Key] = o.Value;
                            tfKnownGood[mat.Name] = opts;
                        }

                    int tfAdapted = 0, remapped = 0;

                    // Pre-compute Map* program indices for each shader model in the testfire bfsha.
                    // Non-Map materials will exclude these programs; Map materials will only use them.
                    var mapProgsByModel = new Dictionary<string, HashSet<int>>();
                    foreach (var sm in hybridBfsha.ShaderModels.Values)
                        mapProgsByModel[sm.Name] = BuildMapProgramIndices(sm, tfKnownGood);

                    foreach (var model in convertedBfres.Models.Values)
                        foreach (var mat in model.Materials.Values)
                        {
                            var sa = mat.ShaderAssign;
                            if (sa?.ShadingModelName == null || sa.ShaderOptions == null) continue;

                            // Check if material's shader model exists in testfire bfsha
                            ShaderModel tfSm = null;
                            foreach (var sm in hybridBfsha.ShaderModels.Values)
                                if (sm.Name == sa.ShadingModelName) { tfSm = sm; break; }

                                if (tfSm != null)
                                {
                                    // Native testfire shader model → use testfire options where possible,
                                    // or find the closest program to preserve the prod material's paint type.
                                    // Map* programs are excluded when matching non-Map materials (and vice versa)
                                    // to prevent minimap UV projection from leaking into field geometry.
                                    Dictionary<string, string> newOpts;
                                    if (tfKnownGood.ContainsKey(mat.Name))
                                        newOpts = tfKnownGood[mat.Name];
                                    else
                                    {
                                        var matOpts = new Dictionary<string, string>();
                                        foreach (var opt in sa.ShaderOptions)
                                            matOpts[opt.Key] = opt.Value;

                                        // Build exclusion set: non-Map materials exclude Map programs, Map materials exclude non-Map programs
                                        HashSet<int> excluded = null;
                                        if (mapProgsByModel.TryGetValue(sa.ShadingModelName, out var mapProgs) && mapProgs.Count > 0)
                                        {
                                            if (IsMapMaterial(mat.Name))
                                            {
                                                // Map material: exclude all NON-Map programs (invert the set)
                                                excluded = new HashSet<int>(Enumerable.Range(0, tfSm.Programs.Count).Where(i => !mapProgs.Contains(i)));
                                            }
                                            else
                                            {
                                                // Non-Map material: exclude Map programs
                                                excluded = mapProgs;
                                            }
                                        }

                                        var match = BfshaHybridBuilder.FindClosestProgram(
                                            mat.Name, sa.ShadingModelName, matOpts, tfSm, excluded);
                                        newOpts = match.AdaptedOptions ?? BfshaHybridBuilder.StripProdOnlyOptions(matOpts, tfSm);
                                    }
                                    sa.ShaderOptions.Clear();
                                    foreach (var kvp in newOpts)
                                        sa.ShaderOptions.Add(kvp.Key, kvp.Value);
                                    tfAdapted++;
                                }
                            else
                            {
                                // Shader model not in testfire bfsha → remap to closest
                                // testfire program using hybrid template matching.
                                // Find the best matching testfire shader model (typically
                                // blitz_uber_fld_np for blitz_uber_obj_np materials, etc.)
                                string bestSmName = null;
                                ShaderModel bestSm = null;
                                // Try name-based fallback: obj_np → fld_np, obj → fld, enm → fld
                                string origName = sa.ShadingModelName;
                                var fallbackNames = new[] {
                                    origName.Replace("_obj_np", "_fld_np"),
                                    origName.Replace("_obj", "_fld"),
                                    origName.Replace("_enm", "_fld"),
                                    "blitz_uber_fld_np",
                                    "blitz_uber_fld"
                                };
                                foreach (var fbName in fallbackNames)
                                {
                                    foreach (var sm in hybridBfsha.ShaderModels.Values)
                                        if (sm.Name == fbName) { bestSm = sm; bestSmName = fbName; break; }
                                    if (bestSm != null) break;
                                }

                                if (bestSm != null)
                                {
                                    // Remap to this testfire shader model
                                    sa.ShadingModelName = bestSmName;

                                    // Use known-good options or find closest program.
                                    // Map* programs excluded for non-Map materials (and vice versa).
                                    Dictionary<string, string> newOpts;
                                    if (tfKnownGood.ContainsKey(mat.Name))
                                        newOpts = tfKnownGood[mat.Name];
                                    else
                                    {
                                        var matOpts = new Dictionary<string, string>();
                                        foreach (var opt in sa.ShaderOptions)
                                            matOpts[opt.Key] = opt.Value;

                                        HashSet<int> excluded = null;
                                        if (mapProgsByModel.TryGetValue(bestSmName, out var mapProgs) && mapProgs.Count > 0)
                                        {
                                            if (IsMapMaterial(mat.Name))
                                                excluded = new HashSet<int>(Enumerable.Range(0, bestSm.Programs.Count).Where(i => !mapProgs.Contains(i)));
                                            else
                                                excluded = mapProgs;
                                        }

                                        var match = BfshaHybridBuilder.FindClosestProgram(
                                            mat.Name, bestSmName, matOpts, bestSm, excluded);
                                        newOpts = match.AdaptedOptions ?? BfshaHybridBuilder.StripProdOnlyOptions(matOpts, bestSm);
                                    }
                                    sa.ShaderOptions.Clear();
                                    foreach (var kvp in newOpts)
                                        sa.ShaderOptions.Add(kvp.Key, kvp.Value);
                                    remapped++;
                                }
                            }
                        }
                    Console.WriteLine($"    Field hybrid: {tfAdapted} testfire, {remapped} remapped to closest TF program");
                }
                else
                {
                    // Normal mode: Strip prod-only options from ALL materials.
                    foreach (var model in convertedBfres.Models.Values)
                        foreach (var mat in model.Materials.Values)
                        {
                            var sa = mat.ShaderAssign;
                            if (sa?.ShadingModelName == null || sa.ShaderOptions == null) continue;

                            ShaderModel tfSm = null;
                            foreach (var sm in hybridBfsha.ShaderModels.Values)
                                if (sm.Name == sa.ShadingModelName) { tfSm = sm; break; }
                            if (tfSm == null) continue;

                            var matOpts = new Dictionary<string, string>();
                            foreach (var opt in sa.ShaderOptions) matOpts[opt.Key] = opt.Value;
                            var clean = BfshaHybridBuilder.StripProdOnlyOptions(matOpts, tfSm);

                            if (clean.Count != matOpts.Count)
                            {
                                sa.ShaderOptions.Clear();
                                foreach (var kvp in clean)
                                    sa.ShaderOptions.Add(kvp.Key, kvp.Value);
                                strippedCount++;
                            }
                        }
                }
                if (strippedCount > 0 && !useTestfireBfsha)
                    Console.WriteLine($"    Stripped prod-only options from {strippedCount} materials");

                // Fill in ALL missing options with shader model defaults.
                int completed = 0;
                foreach (var model in convertedBfres.Models.Values)
                    foreach (var mat in model.Materials.Values)
                    {
                        var sa = mat.ShaderAssign;
                        if (sa?.ShadingModelName == null || sa.ShaderOptions == null) continue;

                        ShaderModel tfSm = null;
                        foreach (var sm in hybridBfsha.ShaderModels.Values)
                            if (sm.Name == sa.ShadingModelName) { tfSm = sm; break; }
                        if (tfSm == null) continue;

                        int added = 0;
                        foreach (var opt in tfSm.StaticOptions)
                        {
                            if (!sa.ShaderOptions.ContainsKey(opt.Key))
                            {
                                sa.ShaderOptions.Add(opt.Key, opt.Value.DefaultChoice);
                                added++;
                            }
                        }
                        // Remove dynamic options from bfres (they shouldn't be there)
                        foreach (var opt in tfSm.DynamicOptions)
                        {
                            if (sa.ShaderOptions.ContainsKey(opt.Key))
                                sa.ShaderOptions.Remove(opt.Key);
                        }
                        // Remove options not in the shader model at all (stale/wrong SM options)
                        var validOpts = new HashSet<string>();
                        for (int i = 0; i < tfSm.StaticOptions.Count; i++)
                            validOpts.Add(tfSm.StaticOptions.GetKey(i));
                        var toRemove = sa.ShaderOptions.Keys
                            .Where(k => !validOpts.Contains(k)).ToList();
                        foreach (var k in toRemove)
                            sa.ShaderOptions.Remove(k);
                        if (added > 0) completed++;
                    }
                if (completed > 0)
                    Console.WriteLine($"    Completed missing options with defaults for {completed} materials");



                // Verify program lookup for all materials.
                // If initial lookup fails, try resolving render-info-derived options
                // (gsys_renderstate, gsys_alpha_test_*) from material RenderInfo and retry.
                // These options are resolved by the game at draw time from RenderInfo,
                // not stored in ShaderOptions.
                Console.WriteLine("  === Verify Program Lookup ===");
                int lookupOk = 0, lookupFail = 0, riFixed = 0;
                foreach (var model in convertedBfres.Models.Values)
                    foreach (var mat in model.Materials.Values)
                    {
                        var sa = mat.ShaderAssign;
                        if (sa?.ShadingModelName == null || sa.ShaderOptions == null) continue;

                        ShaderModel tfSm = null;
                        foreach (var sm in hybridBfsha.ShaderModels.Values)
                            if (sm.Name == sa.ShadingModelName) { tfSm = sm; break; }

                        if (tfSm == null)
                        {
                            Console.WriteLine($"    FAIL {mat.Name}: shader model '{sa.ShadingModelName}' not in bfsha");
                            lookupFail++;
                            continue;
                        }

                        var matOpts = new Dictionary<string, string>();
                        foreach (var opt in sa.ShaderOptions) matOpts[opt.Key] = opt.Value;

                        int progIdx = -1;
                        try { progIdx = tfSm.GetProgramIndex(matOpts); } catch { }

                        if (progIdx >= 0)
                        {
                            // Check if this program has paint sampler (gsys_user2) bound
                            int user2Idx = -1;
                            for (int i = 0; i < tfSm.Samplers.Count; i++)
                                if (tfSm.Samplers.GetKey(i) == "gsys_user2") { user2Idx = i; break; }
                            bool hasPaint = user2Idx >= 0 && progIdx < tfSm.Programs.Count &&
                                tfSm.Programs[progIdx].SamplerIndices != null &&
                                user2Idx < tfSm.Programs[progIdx].SamplerIndices.Count &&
                                tfSm.Programs[progIdx].SamplerIndices[user2Idx].FragmentLocation >= 0;
                            
                            if (mat.Name.Contains("Ground") || mat.Name.Contains("Soil") ||
                                mat.Name.Contains("Floor") || mat.Name.Contains("Asphalt") ||
                                mat.Name.Contains("Grass") || mat.Name.Contains("Map"))
                                Console.WriteLine($"    {mat.Name}: prog={progIdx}, paint={hasPaint}");
                            
                            lookupOk++;
                            continue;
                        }

                        // Fallback: resolve render-info-derived options and retry
                        if (mat.RenderInfos != null)
                        {
                            var saved = new Dictionary<string, string>(matOpts);
                            bool changed = false;

                            // gsys_render_state_mode → gsys_renderstate
                            foreach (var ri in mat.RenderInfos)
                                if (ri.Key == "gsys_render_state_mode")
                                {
                                    var vals = ri.Value.GetValueStrings();
                                    if (vals.Length > 0 && sa.ShaderOptions.ContainsKey("gsys_renderstate"))
                                    {
                                        string gsysRs = vals[0] switch
                                        {
                                            "opaque" => "0", "translucent" => "1",
                                            "mask" => "2", "custom" => "3", _ => null
                                        };
                                        if (gsysRs != null && sa.ShaderOptions["gsys_renderstate"] != gsysRs)
                                        {
                                            sa.ShaderOptions["gsys_renderstate"] = gsysRs;
                                            matOpts["gsys_renderstate"] = gsysRs;
                                            changed = true;
                                        }
                                    }
                                    break;
                                }

                            // gsys_alpha_test_enable
                            foreach (var ri in mat.RenderInfos)
                                if (ri.Key == "gsys_alpha_test_enable")
                                {
                                    var vals = ri.Value.GetValueStrings();
                                    if (vals.Length > 0 && sa.ShaderOptions.ContainsKey("gsys_alpha_test_enable"))
                                    {
                                        string v = vals[0] == "true" ? "1" : "0";
                                        if (sa.ShaderOptions["gsys_alpha_test_enable"] != v)
                                        {
                                            sa.ShaderOptions["gsys_alpha_test_enable"] = v;
                                            matOpts["gsys_alpha_test_enable"] = v;
                                            changed = true;
                                        }
                                    }
                                    break;
                                }

                            // gsys_alpha_test_func
                            foreach (var ri in mat.RenderInfos)
                                if (ri.Key == "gsys_alpha_test_func")
                                {
                                    var vals = ri.Value.GetValueStrings();
                                    if (vals.Length > 0 && sa.ShaderOptions.ContainsKey("gsys_alpha_test_func"))
                                    {
                                        string v = vals[0] switch
                                        {
                                            "never" => "0", "less" => "1", "equal" => "2",
                                            "lequal" => "3", "greater" => "4", "notequal" => "5",
                                            "gequal" => "6", "always" => "7", _ => vals[0]
                                        };
                                        if (sa.ShaderOptions["gsys_alpha_test_func"] != v)
                                        {
                                            sa.ShaderOptions["gsys_alpha_test_func"] = v;
                                            matOpts["gsys_alpha_test_func"] = v;
                                            changed = true;
                                        }
                                    }
                                    break;
                                }

                            if (changed)
                            {
                                try { progIdx = tfSm.GetProgramIndex(matOpts); } catch { }
                                if (progIdx >= 0)
                                {
                                    lookupOk++;
                                    riFixed++;
                                    Console.WriteLine($"    FIXED {mat.Name}: render-info resolved → program {progIdx}");
                                    continue;
                                }
                                else
                                {
                                    // Revert — render-info fix didn't help
                                    foreach (var kvp in saved)
                                        if (sa.ShaderOptions.ContainsKey(kvp.Key))
                                            sa.ShaderOptions[kvp.Key] = kvp.Value;
                                    matOpts = new Dictionary<string, string>(saved);
                                }
                            }
                        }

                        // Brute-force fallback: try all gsys_renderstate values (0-3).
                        // The game resolves this dynamically per render pass, so the
                        // material stores a default that doesn't match any compiled program.
                        if (sa.ShaderOptions.ContainsKey("gsys_renderstate"))
                        {
                            string origRs = sa.ShaderOptions["gsys_renderstate"];
                            for (int rs = 0; rs <= 3; rs++)
                            {
                                string rsStr = rs.ToString();
                                sa.ShaderOptions["gsys_renderstate"] = rsStr;
                                matOpts["gsys_renderstate"] = rsStr;

                                // Also try toggling gsys_alpha_test_enable with each renderstate
                                string[] alphaVals;
                                if (sa.ShaderOptions.ContainsKey("gsys_alpha_test_enable"))
                                {
                                    string curAlpha = sa.ShaderOptions["gsys_alpha_test_enable"];
                                    alphaVals = new string[] { curAlpha, curAlpha == "0" ? "1" : "0" };
                                }
                                else
                                    alphaVals = new string[] { "" };

                                foreach (var av in alphaVals)
                                {
                                    if (av != "" && sa.ShaderOptions.ContainsKey("gsys_alpha_test_enable"))
                                    {
                                        sa.ShaderOptions["gsys_alpha_test_enable"] = av;
                                        matOpts["gsys_alpha_test_enable"] = av;
                                    }

                                    try { progIdx = tfSm.GetProgramIndex(matOpts); } catch { progIdx = -1; }
                                    if (progIdx >= 0)
                                    {
                                        lookupOk++;
                                        riFixed++;
                                        Console.WriteLine($"    FIXED {mat.Name}: gsys_renderstate={rsStr}" +
                                            (av != "" ? $", alpha_test={av}" : "") +
                                            $" → program {progIdx}");
                                        goto nextMaterial;
                                    }
                                }
                            }
                            // Revert if nothing worked
                            sa.ShaderOptions["gsys_renderstate"] = origRs;
                            matOpts["gsys_renderstate"] = origRs;
                        }
                        // Last resort: find closest matching program via weighted key distance
                        var closestMatch = BfshaHybridBuilder.FindClosestProgram(
                            mat.Name, sa.ShadingModelName, matOpts, tfSm);
                        if (closestMatch.AdaptedOptions != null)
                        {
                            sa.ShaderOptions.Clear();
                            foreach (var kvp in closestMatch.AdaptedOptions)
                                sa.ShaderOptions.Add(kvp.Key, kvp.Value);
                            lookupOk++;
                            riFixed++;
                            Console.WriteLine($"    FIXED {mat.Name}: closest program → {closestMatch.ProgramIndex} (distance={closestMatch.DifferingOptions})");
                            goto nextMaterial;
                        }

                        Console.WriteLine($"    FAIL {mat.Name}: GetProgramIndex failed ({matOpts.Count} opts)");
                        lookupFail++;
                        nextMaterial:;
                    }
                Console.WriteLine($"    Lookup OK: {lookupOk}, Fail: {lookupFail}" +
                    (riFixed > 0 ? $" ({riFixed} fixed via render-info)" : ""));


                using var bfresMs = new MemoryStream();
                // CRITICAL: Reset static BufferOffset right before Save.
                // Loading refBfresLocal2 (line ~825) for shader material adaptation
                // overwrites this static field, corrupting buffer offset calculations.
                BufferInfo.BufferOffset = 0;
                convertedBfres.Save(bfresMs);
                var bfresKey = sarcFiles.Keys.First(k => Path.GetExtension(k).Equals(".bfres", StringComparison.OrdinalIgnoreCase));
                outputFiles[bfresKey] = bfresMs.ToArray();
                Console.WriteLine($"    Bfres: {convertedBfres.Models.Count} models, Major2={convertedBfres.VersionMajor2}");

                // Save hybrid bfsha (only if the original SARC had one)
                var bfshaKey = sarcFiles.Keys.FirstOrDefault(k => Path.GetExtension(k).Equals(".bfsha", StringComparison.OrdinalIgnoreCase));
                if (bfshaKey != null)
                {
                    // Strip unused shader models from the hybrid to reduce size.
                    // The full hybrid includes ALL models from the reference bfsha + Obj models,
                    // but small Obj files may only use 1-2 models. Including all models inflates
                    // the output (e.g. 3.7MB bfsha for a 2-material Sphere), causing OOM crashes
                    // in the game's fixed-size model loading heap.
                    var usedModels = new HashSet<string>();
                    foreach (var model in convertedBfres.Models.Values)
                        foreach (var mat in model.Materials.Values)
                            if (mat.ShaderAssign?.ShadingModelName != null)
                                usedModels.Add(mat.ShaderAssign.ShadingModelName);

                    if (usedModels.Count > 0 && usedModels.Count < hybridBfsha.ShaderModels.Count)
                    {
                        var toRemove = new List<string>();
                        foreach (var sm in hybridBfsha.ShaderModels)
                            if (!usedModels.Contains(sm.Value.Name))
                                toRemove.Add(sm.Key);
                        foreach (var key in toRemove)
                            hybridBfsha.ShaderModels.Remove(key);
                        if (toRemove.Count > 0)
                            Console.WriteLine($"    Stripped {toRemove.Count} unused shader models (keeping {hybridBfsha.ShaderModels.Count} used)");
                    }

                    using var bfshaMs = new MemoryStream();
                    hybridBfsha.Save(bfshaMs);
                    outputFiles[bfshaKey] = bfshaMs.ToArray();
                    Console.WriteLine($"    Bfsha: {bfshaMs.Length} bytes (hybrid)");

                // Round-trip verification: reload saved bfsha and re-test program lookup
                var reloadedBfsha = new BfshaFile(new MemoryStream(bfshaMs.ToArray()));
                int rtOk = 0, rtFail = 0;
                foreach (var model in convertedBfres.Models.Values)
                    foreach (var mat in model.Materials.Values)
                    {
                        var sa = mat.ShaderAssign;
                        if (sa?.ShadingModelName == null || sa.ShaderOptions == null) continue;

                        ShaderModel rlSm = null;
                        foreach (var sm in reloadedBfsha.ShaderModels.Values)
                            if (sm.Name == sa.ShadingModelName) { rlSm = sm; break; }
                        if (rlSm == null) { rtFail++; continue; }

                        var opts = new Dictionary<string, string>();
                        foreach (var o in sa.ShaderOptions) opts[o.Key] = o.Value;
                        int rtIdx = -1;
                        try { rtIdx = rlSm.GetProgramIndex(opts); } catch { }

                        if (rtIdx >= 0)
                            rtOk++;
                        else
                        {
                            rtFail++;
                            Console.WriteLine($"    RT-FAIL {mat.Name}: reloaded GetProgramIndex=-1 (model '{sa.ShadingModelName}', {opts.Count} opts)");
                        }
                    }
                Console.WriteLine($"    Round-trip: OK={rtOk}, FAIL={rtFail}");
                } // end if (bfshaKey != null)
                else
                    Console.WriteLine("    No bfsha in SARC — skipping shader archive");

                // Repack SARC
                byte[] sarcData = Oead.SarcWrite(outputFiles);

                // Recompress with Yaz0
                Console.WriteLine($"  Compressing {sarcData.Length} bytes...");
                byte[] compressed = Oead.Yaz0Compress(sarcData, dataAlignment: 0x2000);

                // Write output SZS
                string outputPath = Path.Combine(outputDir, szsName);
                File.WriteAllBytes(outputPath, compressed);
                Console.WriteLine($"  ✓ Saved: {outputPath} ({compressed.Length} bytes)");
                success++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ✗ FAILED: {ex.Message}");
                Console.Error.WriteLine($"    {ex.StackTrace}");
                failed++;
            }
        }

        Console.WriteLine($"\n=== Batch complete: {success} converted, {skipped} skipped, {failed} failed ===");
        return failed > 0 ? 2 : 0;
    }

    /// <summary>
    /// Diagnostic: list shader models/programs from SZS files.
    /// Usage: --szs-diag file1.szs [file2.szs ...]
    /// </summary>
    static int RunSzsDiag(string[] szsFiles)
    {
        foreach (var szs in szsFiles)
        {
            Console.WriteLine($"\n=== {Path.GetFileName(szs)} ===");
            try
            {
                var decompressed = Oead.Yaz0DecompressFile(szs);
                var files = Oead.SarcRead(decompressed);

                Console.WriteLine($"  Files: {files.Count}");
                foreach (var f in files.OrderBy(x => x.Key))
                {
                    string ext = Path.GetExtension(f.Key).ToLowerInvariant();
                    Console.WriteLine($"    {f.Key} ({f.Value.Length} bytes)");

                    if (ext == ".bfsha")
                    {
                        try
                        {
                            var bfsha = new BfshaFile(new MemoryStream(f.Value));
                            Console.WriteLine($"      Version: {bfsha.BinHeader.VersionMajor}.{bfsha.BinHeader.VersionMinor}.{bfsha.BinHeader.VersionMicro}");
                            Console.WriteLine($"      ShaderModels: {bfsha.ShaderModels.Count}");
                            for (int i = 0; i < bfsha.ShaderModels.Count; i++)
                            {
                                var sm = bfsha.ShaderModels.Values.ElementAt(i);
                                Console.WriteLine($"        [{i}] {sm.Name}: {sm.Programs.Count} programs, " +
                                    $"{sm.UniformBlocks.Count} uniform blocks, " +
                                    $"{sm.StaticOptions.Count}+{sm.DynamicOptions.Count} options");
                                if (sm.BlockIndices != null)
                                    Console.WriteLine($"          BlockIndices: [{string.Join(",", sm.BlockIndices)}]");
                                foreach (var ub in sm.UniformBlocks)
                                    Console.WriteLine($"          UB[{ub.Value.Index}] '{ub.Key}' Type={ub.Value.Type} Size={ub.Value.Size}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"      ERROR loading bfsha: {ex.Message}");
                        }
                    }
                    else if (ext == ".bfres")
                    {
                        try
                        {
                            var bfres = new BfresLibrary.ResFile(new MemoryStream(f.Value));
                            Console.WriteLine($"      Version: {bfres.VersionMajor}.{bfres.VersionMajor2}.{bfres.VersionMinor}.{bfres.VersionMinor2}");
                            Console.WriteLine($"      Models: {bfres.Models.Count}");
                            foreach (var model in bfres.Models.Values)
                            {
                                Console.WriteLine($"        Model '{model.Name}': {model.Materials.Count} materials");
                                // Show unique shader model names used by materials
                                var shaderNames = model.Materials.Values
                                    .Select(m => m.ShaderAssign?.ShadingModelName ?? "(none)")
                                    .Distinct().OrderBy(x => x);
                                foreach (var sn in shaderNames)
                                    Console.WriteLine($"          ShaderModel: {sn}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"      ERROR loading bfres: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ERROR: {ex.Message}");
            }
        }
        return 0;
    }

    /// <summary>
    /// Raw-patch mode: patch bfres version bytes without re-serialization.
    /// Bfsha is downgraded via BfshaConverter (necessary for structure changes).
    /// Bfres is ONLY version-patched in raw binary — no BfresLibrary load/save.
    /// Usage: --raw-patch-szs input.szs output.szs
    /// </summary>
    static int RunRawPatchMode(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: --raw-patch-szs input.szs output.szs");
            return 1;
        }

        string inputSzs = args[0];
        string outputSzs = args[1];

        Console.WriteLine($"Raw-patching: {Path.GetFileName(inputSzs)}");

        // Decompress
        byte[] decompressed = Oead.Yaz0DecompressFile(inputSzs);
        var files = Oead.SarcRead(decompressed);

        var outputFiles = new Dictionary<string, byte[]>();

        foreach (var entry in files)
        {
            string ext = Path.GetExtension(entry.Key).ToLowerInvariant();

            if (ext == ".bfres")
            {
                // RAW patch: just change version bytes from V8 (0x00080000) to V5 (0x00050003)
                byte[] patched = (byte[])entry.Value.Clone();

                // Find FRES magic
                int fresIdx = -1;
                for (int i = 0; i < Math.Min(patched.Length, 16); i++)
                {
                    if (patched[i] == 'F' && patched[i+1] == 'R' && patched[i+2] == 'E' && patched[i+3] == 'S')
                    {
                        fresIdx = i;
                        break;
                    }
                }

                if (fresIdx >= 0)
                {
                    // Version is at offset +8 from FRES, little-endian 32-bit
                    uint oldVer = BitConverter.ToUInt32(patched, fresIdx + 8);
                    // Target: V5.0.3 = 0x00050003
                    uint newVer = 0x00050003;
                    byte[] verBytes = BitConverter.GetBytes(newVer);
                    Array.Copy(verBytes, 0, patched, fresIdx + 8, 4);
                    Console.WriteLine($"  Bfres: patched version 0x{oldVer:X8} → 0x{newVer:X8} (raw binary patch)");
                }
                outputFiles[entry.Key] = patched;
            }
            else if (ext == ".bfsha")
            {
                // Bfsha needs structure changes (StorageBuffers, BlockIndices), so use BfshaConverter
                Console.WriteLine($"  Bfsha: downgrading {entry.Key} via BfshaConverter");
                var bfsha = new BfshaFile(new MemoryStream(entry.Value));
                Console.WriteLine($"    V{bfsha.BinHeader.VersionMajor} → V5, {bfsha.ShaderModels.Count} shader models");
                BfshaConverter.Downgrade(bfsha);
                using var ms = new MemoryStream();
                bfsha.Save(ms);
                outputFiles[entry.Key] = ms.ToArray();
                Console.WriteLine($"    ✓ Downgraded to V{bfsha.BinHeader.VersionMajor}");
            }
            else
            {
                outputFiles[entry.Key] = entry.Value;
            }
        }

        // Repack & compress
        byte[] sarcData = Oead.SarcWrite(outputFiles);
        Console.WriteLine($"  Compressing {sarcData.Length} bytes...");
        byte[] compressed = Oead.Yaz0Compress(sarcData, dataAlignment: 0x2000);

        Directory.CreateDirectory(Path.GetDirectoryName(outputSzs) ?? ".");
        File.WriteAllBytes(outputSzs, compressed);
        Console.WriteLine($"  ✓ Saved: {outputSzs} ({compressed.Length} bytes)");

        return 0;
    }

    /// <summary>
    /// Layout fix: add/modify PartsPane entries in an SZS layout archive.
    /// Handles the double-SARC structure: SZS (Yaz0) → outer SARC → inner .arc SARC → BFLYT.
    ///
    /// Operations:
    ///   --add <parent_pane> <pane_name> <layout_file>   Add a new PartsPane
    ///   --rename <pane_name> <new_layout_file>           Change a PartsPane's LayoutFileName
    ///
    /// Usage:
    ///   --layout-add-parts input.szs output.szs --add N_All_00 L_Back_00 KeyDecideIcon_00
    ///   --layout-add-parts input.szs output.szs --rename L_KeyIcon_00 KeyDecideIcon_00
    /// </summary>
    static int RunLayoutAddParts(string[] args)
    {
        Console.WriteLine("\n=== LAYOUT ADD/MODIFY PARTS ===");

        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: --layout-add-parts input.szs output.szs [operations...]");
            Console.Error.WriteLine("  --add <parent_pane> <pane_name> <layout_file>");
            Console.Error.WriteLine("  --rename <pane_name> <new_layout_file>");
            return 1;
        }

        string inputSzs = args[0];
        string outputSzs = args[1];

        // Parse operations
        var addOps = new List<(string ParentPane, string PaneName, string LayoutFile)>();
        var renameOps = new List<(string PaneName, string NewLayoutFile)>();

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--add" && i + 3 < args.Length)
            {
                addOps.Add((args[i + 1], args[i + 2], args[i + 3]));
                i += 3;
            }
            else if (args[i] == "--rename" && i + 2 < args.Length)
            {
                renameOps.Add((args[i + 1], args[i + 2]));
                i += 2;
            }
            else
            {
                Console.Error.WriteLine($"Unknown operation or missing args at: {args[i]}");
                return 1;
            }
        }

        if (addOps.Count == 0 && renameOps.Count == 0)
        {
            Console.Error.WriteLine("No operations specified. Use --add or --rename.");
            return 1;
        }

        // Step 1: Decompress outer SZS
        Console.WriteLine($"Reading: {Path.GetFileName(inputSzs)}");
        byte[] outerSarc = Oead.Yaz0DecompressFile(inputSzs);
        var outerFiles = Oead.SarcRead(outerSarc);
        Console.WriteLine($"  Outer SARC: {outerFiles.Count} file(s)");

        // Step 2: Find the inner .arc SARC
        var arcEntry = outerFiles.FirstOrDefault(f => Path.GetExtension(f.Key).Equals(".arc", StringComparison.OrdinalIgnoreCase));
        if (arcEntry.Value == null)
        {
            Console.Error.WriteLine("ERROR: No .arc file found in outer SARC");
            return 1;
        }
        Console.WriteLine($"  Inner archive: {arcEntry.Key}");

        var innerFiles = Oead.SarcRead(arcEntry.Value);
        Console.WriteLine($"  Inner SARC: {innerFiles.Count} file(s)");
        foreach (var f in innerFiles)
            Console.WriteLine($"    {f.Key} ({f.Value.Length} bytes)");

        // Step 3: Find and parse the BFLYT
        var bflytEntry = innerFiles.FirstOrDefault(f => f.Key.EndsWith(".bflyt", StringComparison.OrdinalIgnoreCase));
        if (bflytEntry.Value == null)
        {
            Console.Error.WriteLine("ERROR: No .bflyt file found in inner SARC");
            return 1;
        }
        Console.WriteLine($"\n  Parsing BFLYT: {bflytEntry.Key}");

        var bflyt = new BflytFile(new MemoryStream(bflytEntry.Value));
        Console.WriteLine($"  Root pane: {bflyt.Root?.Name}");

        // Print current pane tree summary
        Console.WriteLine("\n  Current pane tree:");
        PrintPaneTree(bflyt.Root, 2);

        // Step 4: Apply rename operations
        foreach (var op in renameOps)
        {
            var pane = FindPane(bflyt.Root, op.PaneName);
            if (pane == null)
            {
                Console.Error.WriteLine($"  ✗ Pane '{op.PaneName}' not found for rename");
                return 1;
            }
            if (pane is not PartsPane parts)
            {
                Console.Error.WriteLine($"  ✗ Pane '{op.PaneName}' is {pane.GetType().Name}, not PartsPane");
                return 1;
            }
            string oldLayout = parts.LayoutFileName;
            parts.LayoutFileName = op.NewLayoutFile;
            Console.WriteLine($"  ✓ Renamed '{op.PaneName}' LayoutFile: \"{oldLayout}\" → \"{op.NewLayoutFile}\"");
        }

        // Step 5: Apply add operations
        foreach (var op in addOps)
        {
            var parentPane = FindPane(bflyt.Root, op.ParentPane);
            if (parentPane == null)
            {
                Console.Error.WriteLine($"  ✗ Parent pane '{op.ParentPane}' not found for add");
                return 1;
            }

            // Check if pane already exists
            if (FindPane(bflyt.Root, op.PaneName) != null)
            {
                Console.WriteLine($"  ⚠ Pane '{op.PaneName}' already exists, skipping add");
                continue;
            }

            var newParts = new PartsPane()
            {
                Name = op.PaneName,
                LayoutFileName = op.LayoutFile,
                Visible = true,
                Alpha = 255,
                Flags1 = 0x01,  // visible flag
                Translate = Vector3.Zero,
                Rotate = Vector3.Zero,
                Scale = Vector2.One,
                Width = 0,
                Height = 0,
                MagnifyX = 1.0f,
                MagnifyY = 1.0f,
            };
            newParts.Parent = parentPane;
            Console.WriteLine($"  ✓ Added pane '{op.PaneName}' → LayoutFile=\"{op.LayoutFile}\" under '{op.ParentPane}'");
        }

        // Step 6: Print modified pane tree
        Console.WriteLine("\n  Modified pane tree:");
        PrintPaneTree(bflyt.Root, 2);

        // Step 7: Save the modified BFLYT
        using var bflytStream = new MemoryStream();
        bflyt.Save(bflytStream);
        byte[] newBflytData = bflytStream.ToArray();
        Console.WriteLine($"\n  BFLYT: orig={bflytEntry.Value.Length} → new={newBflytData.Length} bytes");

        // Step 8: Rebuild inner SARC
        var newInnerFiles = new Dictionary<string, byte[]>(innerFiles);
        newInnerFiles[bflytEntry.Key] = newBflytData;
        byte[] newInnerSarc = Oead.SarcWrite(newInnerFiles);
        Console.WriteLine($"  Inner SARC: orig={arcEntry.Value.Length} → new={newInnerSarc.Length} bytes");

        // Step 9: Rebuild outer SARC
        var newOuterFiles = new Dictionary<string, byte[]>(outerFiles);
        newOuterFiles[arcEntry.Key] = newInnerSarc;
        byte[] newOuterSarc = Oead.SarcWrite(newOuterFiles);
        Console.WriteLine($"  Outer SARC: orig={outerSarc.Length} → new={newOuterSarc.Length} bytes");

        // Step 10: Compress and save
        byte[] compressed = Oead.Yaz0Compress(newOuterSarc);
        File.WriteAllBytes(outputSzs, compressed);
        Console.WriteLine($"\n  ✓ Saved: {outputSzs} ({compressed.Length} bytes)");

        // Verification: re-read and parse
        Console.WriteLine("\n  Verifying output...");
        byte[] verifySarc = Oead.Yaz0Decompress(File.ReadAllBytes(outputSzs));
        var verifyOuter = Oead.SarcRead(verifySarc);
        var verifyArc = verifyOuter.First(f => f.Key.EndsWith(".arc"));
        var verifyInner = Oead.SarcRead(verifyArc.Value);
        var verifyBflyt = verifyInner.First(f => f.Key.EndsWith(".bflyt"));
        var verifyLayout = new BflytFile(new MemoryStream(verifyBflyt.Value));
        Console.WriteLine($"  ✓ Verified: root={verifyLayout.Root?.Name}, children={verifyLayout.Root?.Children.Count}");
        PrintPaneTree(verifyLayout.Root, 2);

        Console.WriteLine("\n=== Layout fix complete ===");
        return 0;
    }

    /// <summary>
    /// Find a pane by name in the pane tree (depth-first search).
    /// </summary>
    static Pane FindPane(Pane root, string name)
    {
        if (root == null) return null;
        if (root.Name == name) return root;
        foreach (var child in root.Children)
        {
            var found = FindPane(child, name);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Print a pane tree for diagnostic output.
    /// </summary>
    static void PrintPaneTree(Pane pane, int depth)
    {
        if (pane == null) return;
        string indent = new string(' ', depth * 2);
        string extra = "";
        if (pane is PartsPane parts)
            extra = $" → LayoutFile=\"{parts.LayoutFileName}\"";
        Console.WriteLine($"{indent}[{pane.Magic}] \"{pane.Name}\"{extra}");
        foreach (var child in pane.Children)
            PrintPaneTree(child, depth + 1);
    }

    /// <summary>
    /// Build a BARS file for a given SLink user by:
    ///   1. Loading the SLink2DB.szs and looking up the user
    ///   2. Extracting all RuntimeAssetName values from the user's asset calls
    ///   3. Searching provided BARS directories for those BWAVs
    ///   4. Building a new BARS with found tracks + silent placeholders for missing
    ///
    /// Usage:
    ///   --bars-build --slink SLink2DB.szs --user UserName --search-dirs dir1 [dir2 ...] --output output_dir
    /// </summary>
    static int RunBarsBuild(string[] args)
    {
        Console.WriteLine("\n=== BARS BUILD ===");

        string slinkPath = null;
        string outputPath = null;
        var userNames = new List<string>();
        var searchDirs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--slink" when i + 1 < args.Length:
                    slinkPath = args[++i];
                    break;
                case "--user" when i + 1 < args.Length:
                    userNames.Add(args[++i]);
                    break;
                case "--search-dirs":
                    i++;
                    while (i < args.Length && !args[i].StartsWith("--"))
                        searchDirs.Add(args[i++]);
                    i--;
                    break;
                case "--output" when i + 1 < args.Length:
                    outputPath = args[++i];
                    break;
            }
        }

        if (slinkPath == null || userNames.Count == 0 || searchDirs.Count == 0)
        {
            Console.Error.WriteLine("Usage: --bars-build --slink <SLink2DB.szs> --user <name> [--user <name2> ...] --search-dirs <dir1> [dir2 ...] [--output <dir>]");
            return 1;
        }

        if (outputPath == null) outputPath = ".";
        Directory.CreateDirectory(outputPath);

        // Step 1: Load SLink database via WoomLink
        Console.WriteLine($"  Loading SLink: {slinkPath}");
        WoomLink.Ex.Pointer<byte> sdata;
        try { sdata = LoadSlinkYaz0SarcFile(slinkPath); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ERROR loading SLink: {ex.Message}");
            return 1;
        }

        var ssystem = SystemSLink.GetInstance();
        ssystem.Initialize(96);
        var loadOk = ssystem.LoadResource(sdata.PointerValue);
        if (!loadOk) { Console.Error.WriteLine("  ERROR: Failed to parse SLink resource"); return 1; }
        ssystem.AllocGlobalProperty(0);
        ssystem.FixGlobalPropertyDefinition();
        Console.WriteLine($"  Loaded {ssystem.ResourceBuffer.RSP.NumUser} SLink users");

        // Step 2: Index all BWAVs from search directories
        Console.WriteLine($"  Indexing BARS files from {searchDirs.Count} search dir(s)...");
        var bwavIndex = new Dictionary<string, (string barsPath, BarsFile.BarsTrack track)>();
        int totalBarsFiles = 0;

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) { Console.Error.WriteLine($"  WARNING: Dir not found: {dir}"); continue; }
            foreach (var barsPath in Directory.GetFiles(dir, "*.bars"))
            {
                totalBarsFiles++;
                try
                {
                    var bars = BarsFile.FromFile(barsPath);
                    foreach (var track in bars.Tracks)
                        if (!bwavIndex.ContainsKey(track.Name))
                            bwavIndex[track.Name] = (barsPath, track);
                }
                catch { }
            }
        }
        Console.WriteLine($"  Indexed {bwavIndex.Count} unique BWAVs from {totalBarsFiles} BARS files");

        // Step 3: For each user, extract RuntimeAssetNames and build BARS
        foreach (var userName in userNames)
        {
            Console.WriteLine($"\n  --- Building BARS for: {userName} ---");

            var runtimeAssetNames = GetSLinkRuntimeAssetNames(ssystem, userName);
            if (runtimeAssetNames == null)
            {
                Console.Error.WriteLine($"  ERROR: User '{userName}' not found in SLink database");
                continue;
            }

            var uniqueNames = runtimeAssetNames.Distinct().ToList();
            Console.WriteLine($"  SLink references {uniqueNames.Count} RuntimeAssetName(s):");
            foreach (var name in uniqueNames) Console.WriteLine($"    {name}");

            var newBars = new BarsFile();
            int foundCount = 0, silentCount = 0;

            foreach (var assetName in uniqueNames)
            {
                if (bwavIndex.TryGetValue(assetName, out var found))
                {
                    newBars.Tracks.Add(new BarsFile.BarsTrack
                    {
                        Name = assetName,
                        AmtaData = found.track.AmtaData,
                        BwavData = found.track.BwavData,
                    });
                    Console.WriteLine($"  ✓ {assetName} ← {Path.GetFileName(found.barsPath)}");
                    foundCount++;
                }
                else
                {
                    newBars.Tracks.Add(new BarsFile.BarsTrack
                    {
                        Name = assetName,
                        AmtaData = BarsFile.CreateSilentAmta(assetName),
                        BwavData = BarsFile.CreateSilentBwav(),
                    });
                    Console.WriteLine($"  ⚠ {assetName} ← silent placeholder (not found)");
                    silentCount++;
                }
            }

            string outFile = Path.Combine(outputPath, $"{userName}.bars");
            newBars.Save(outFile);
            Console.WriteLine($"\n  ✓ Saved: {outFile} ({new FileInfo(outFile).Length} bytes)");
            Console.WriteLine($"    {newBars.Tracks.Count} tracks ({foundCount} found, {silentCount} silent)");

            try
            {
                var verify = BarsFile.FromFile(outFile);
                Console.WriteLine($"  ✓ Verified: {verify.Tracks.Count} tracks readable");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ✗ Verification failed: {ex.Message}");
            }
        }

        Console.WriteLine("\n=== BARS build complete ===");
        return 0;
    }

    /// <summary>
    /// Extract RuntimeAssetName values from a SLink user's asset call table.
    /// Uses WoomLink's UserPrinter to get text output, then parses RuntimeAssetName fields.
    /// </summary>
    static List<string> GetSLinkRuntimeAssetNames(WoomLink.xlink2.System system, string userName)
    {
        ref var param = ref system.ResourceBuffer.RSP;
        var idx = WoomLink.Utils.BinarySearch<uint, uint>(
            param.UserDataHashesSpan, HashCrc32.CalcStringHash(userName));
        if (idx < 0) return null;

        var user = param.UserDataPointersSpan[idx];
        ResourceParamCreator.CreateUserBinParam(out var userParam, user, in system.GetParamDefineTable());

        var printer = new UserPrinter();
        printer.Print(system, in param.Common, in userParam);
        string output = printer.Writer.ToString();

        var names = new List<string>();
        var regex = new Regex(@"RuntimeAssetName\s*=\s*""([^""]+)""");
        foreach (Match match in regex.Matches(output))
            names.Add(match.Groups[1].Value);

        return names;
    }

    /// <summary>Load a Yaz0-compressed SARC (SZS) onto WoomLink's fake heap for SLink parsing.</summary>
    static WoomLink.Ex.Pointer<byte> LoadSlinkYaz0SarcFile(string path)
    {
        // Use HammerheadConverter's existing Yaz0/SARC tools
        byte[] decompressed = Oead.Yaz0DecompressFile(path);
        var files = Oead.SarcRead(decompressed);
        if (files.Count != 1)
            throw new Exception($"Expected 1 file in SARC, found {files.Count}");
        var data = files.Values.First();
        var ptr = FakeHeap.AllocateT<byte>(data.Length);
        data.AsSpan().CopyTo(ptr.AsSpan(data.Length));
        return ptr;
    }

    static void PrintUsage()
    {
        Console.WriteLine(@"
HammerheadConverter — Bfsha/Bfres Prod-to-Testfire Converter

Usage:
  HammerheadConverter [options]

Modes:
  1. Downgrade mode (V7→V5 bfsha):
     --prod-bfsha <path>      Path to the prod bfsha file (V7)
     --output-dir <path>      Directory to write converted files

  2. Hybrid mode (single file, testfire bfsha + adapted bfres):
     --shadict <path>         Path to shadict directory with testfire SZS archives
     --prod-bfres <path>      Path to the prod bfres file
     --testfire-bfres <path>  Path to the testfire bfres template file
     --testfire-bfsha <path>  Path to the testfire bfsha file to use
     --output-dir <path>      Directory to write converted files

  3. Batch SZS mode (convert all SZS in a directory):
     --batch-szs <path>       Directory of prod SZS files to convert
     --ref-szs <path>         Reference testfire SZS (provides bfres + bfsha template)
     --shadict <path>         Path to shadict directory with testfire SZS archives
     --output-dir <path>      Directory to write converted SZS files

  4. Layout fix (add/modify parts panes in layout SZS):
     --layout-add-parts input.szs output.szs [operations...]
       --add <parent> <name> <layout>    Add a new PartsPane under parent
       --rename <name> <new_layout>      Change a PartsPane's layout file ref

     Example (fix Plz_AmiiboOperation_00 for Testfire):
       --layout-add-parts Plz_AmiiboOperation_00.Nin_NX_NVN.szs fixed.szs \
         --add N_All_00 L_Back_00 KeyDecideIcon_00 \
         --rename L_KeyIcon_00 KeyDecideIcon_00 \
         --rename L_KeyIcon_01 KeyDecideIcon_00

Optional:
  --prod-bfres <path>      Path to the prod bfres file (downgrade mode)
  --testfire-bfres <path>  Path to the testfire bfres template (downgrade mode)
  --diag                   Run diagnostic mode on bfsha files

Examples:
  # Batch SZS conversion:
  HammerheadConverter \
    --batch-szs /path/to/prod-szs/ \
    --ref-szs /path/to/testfire/Fld_Ditch01.szs \
    --shadict /path/to/shadict \
    --output-dir converted/

  # Single-file hybrid mode:
  HammerheadConverter \
    --shadict /path/to/shadict \
    --prod-bfres output.bfres \
    --testfire-bfres testfire.bfres \
    --testfire-bfsha Blitz_UBER.Nin_NX_NVN.bfsha \
    --output-dir converted/
");
    }

    static int RunPaintDiag(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: --paint-diag prod.szs testfire.szs"); return 1; }
        
        // Compare SZS file structure first
        var prodData = File.ReadAllBytes(args[0]);
        var prodSarc = Oead.SarcRead(Oead.Yaz0Decompress(prodData));
        var tfData = File.ReadAllBytes(args[1]);
        var tfSarc = Oead.SarcRead(Oead.Yaz0Decompress(tfData));
        
        Console.WriteLine($"=== SZS File Structure ===");
        Console.WriteLine($"Prod: {prodSarc.Count} files, Testfire: {tfSarc.Count} files");
        var prodOnly = prodSarc.Keys.Except(tfSarc.Keys).OrderBy(x=>x).ToList();
        var tfOnly = tfSarc.Keys.Except(prodSarc.Keys).OrderBy(x=>x).ToList();
        var sharedFiles = prodSarc.Keys.Intersect(tfSarc.Keys).OrderBy(x=>x).ToList();
        if (prodOnly.Any()) { Console.WriteLine("PROD-ONLY:"); foreach (var f in prodOnly) Console.WriteLine($"  {prodSarc[f].Length,10} {f}"); }
        if (tfOnly.Any()) { Console.WriteLine("TESTFIRE-ONLY:"); foreach (var f in tfOnly) Console.WriteLine($"  {tfSarc[f].Length,10} {f}"); }
        Console.WriteLine($"SHARED: {sharedFiles.Count} files");
        foreach (var f in sharedFiles) {
            int pSize = prodSarc[f].Length, tSize = tfSarc[f].Length;
            if (pSize != tSize) Console.WriteLine($"  SIZE DIFF: {f}: prod={pSize} tf={tSize}");
        }
        Console.WriteLine();

        // Compare bfsha binding metadata
        {
            var pBfshaData = prodSarc.First(kv => kv.Key.EndsWith(".bfsha")).Value;
            var tBfshaData = tfSarc.First(kv => kv.Key.EndsWith(".bfsha")).Value;
            var pBfsha = new BfshaFile(new MemoryStream(pBfshaData));
            var tBfsha = new BfshaFile(new MemoryStream(tBfshaData));
            // Downgrade prod for fair comparison
            pBfsha = BfshaConverter.Downgrade(pBfsha);

            Console.WriteLine("=== BFSHA Binding Comparison ===");
            foreach (var pSm in pBfsha.ShaderModels.Values)
            {
                ShaderModel tSm = null;
                foreach (var t in tBfsha.ShaderModels.Values)
                    if (t.Name == pSm.Name) { tSm = t; break; }
                if (tSm == null) continue;

                Console.WriteLine($"\n[{pSm.Name}] Prod: {pSm.Programs.Count} progs, TF: {tSm.Programs.Count} progs");

                // Scalar properties
                Console.WriteLine($"  StaticKeyLength: prod={pSm.StaticKeyLength} tf={tSm.StaticKeyLength}");
                Console.WriteLine($"  DynamicKeyLength: prod={pSm.DynamicKeyLength} tf={tSm.DynamicKeyLength}");
                Console.WriteLine($"  DefaultProgramIndex: prod={pSm.DefaultProgramIndex} tf={tSm.DefaultProgramIndex}");
                Console.WriteLine($"  Unknown2: prod={pSm.Unknown2} tf={tSm.Unknown2}");
                Console.WriteLine($"  BlockIndices: prod=[{string.Join(",", pSm.BlockIndices ?? Array.Empty<byte>())}] tf=[{string.Join(",", tSm.BlockIndices ?? Array.Empty<byte>())}]");
                Console.WriteLine($"  UnknownIndices2: prod=[{string.Join(",", pSm.UnknownIndices2 ?? Array.Empty<byte>())}] tf=[{string.Join(",", tSm.UnknownIndices2 ?? Array.Empty<byte>())}]");
                Console.WriteLine($"  MaxVSRingItemSize: prod={pSm.MaxVSRingItemSize} tf={tSm.MaxVSRingItemSize}");
                Console.WriteLine($"  MaxRingItemSize: prod={pSm.MaxRingItemSize} tf={tSm.MaxRingItemSize}");
                Console.WriteLine($"  BnshFile.Variations: prod={pSm.BnshFile.Variations.Count} tf={tSm.BnshFile.Variations.Count}");

                // DynamicOptions
                Console.WriteLine($"  DynamicOptions: prod={pSm.DynamicOptions.Count} tf={tSm.DynamicOptions.Count}");
                for (int i = 0; i < Math.Max(pSm.DynamicOptions.Count, tSm.DynamicOptions.Count); i++)
                {
                    string pName = i < pSm.DynamicOptions.Count ? pSm.DynamicOptions.GetKey(i) : "(none)";
                    string tName = i < tSm.DynamicOptions.Count ? tSm.DynamicOptions.GetKey(i) : "(none)";
                    if (pName != tName) Console.WriteLine($"    DynOpt[{i}]: prod={pName} tf={tName}");
                }

                // StorageBuffers
                if (pSm.StorageBuffers.Count > 0 || tSm.StorageBuffers.Count > 0)
                    Console.WriteLine($"  StorageBuffers: prod={pSm.StorageBuffers.Count} tf={tSm.StorageBuffers.Count}");
                // Images
                if (pSm.Images.Count > 0 || tSm.Images.Count > 0)
                    Console.WriteLine($"  Images: prod={pSm.Images.Count} tf={tSm.Images.Count}");

                // Per-program summary (program 0 details)
                if (pSm.Programs.Count > 0 && tSm.Programs.Count > 0)
                {
                    var pp = pSm.Programs[0]; var tp = tSm.Programs[0];
                    Console.WriteLine($"  Program[0]: VariationIdx=prod:{pp.VariationIndex}/tf:{tp.VariationIndex}");
                    Console.WriteLine($"    UsedAttrFlags: prod=0x{pp.UsedAttributeFlags:X8} tf=0x{tp.UsedAttributeFlags:X8}");
                    Console.WriteLine($"    SamplerIndices: prod={pp.SamplerIndices?.Count} tf={tp.SamplerIndices?.Count}");
                    Console.WriteLine($"    UBIndices: prod={pp.UniformBlockIndices?.Count} tf={tp.UniformBlockIndices?.Count}");
                    Console.WriteLine($"    ImageIndices: prod={pp.ImageIndices?.Count} tf={tp.ImageIndices?.Count}");
                    Console.WriteLine($"    StorageBufIndices: prod={pp.StorageBufferIndices?.Count} tf={tp.StorageBufferIndices?.Count}");
                    // Compare sampler binding locations for diffs
                    if (pp.SamplerIndices != null && tp.SamplerIndices != null)
                        for (int i = 0; i < Math.Min(pp.SamplerIndices.Count, tp.SamplerIndices.Count); i++)
                        {
                            var pi = pp.SamplerIndices[i]; var ti = tp.SamplerIndices[i];
                            if (pi.VertexLocation != ti.VertexLocation || pi.FragmentLocation != ti.FragmentLocation)
                                Console.WriteLine($"    SmpIdx[{i}]({(i<pSm.Samplers.Count?pSm.Samplers.GetKey(i):"?")}): prod=v{pi.VertexLocation}/f{pi.FragmentLocation} tf=v{ti.VertexLocation}/f{ti.FragmentLocation}");
                        }
                }

                // Compare UB dict order
                Console.WriteLine($"  UB count: prod={pSm.UniformBlocks.Count} tf={tSm.UniformBlocks.Count}");
                for (int i = 0; i < Math.Min(pSm.UniformBlocks.Count, tSm.UniformBlocks.Count); i++)
                {
                    string pName = pSm.UniformBlocks.GetKey(i);
                    string tName = tSm.UniformBlocks.GetKey(i);
                    int pSize2 = pSm.UniformBlocks[i].Size;
                    int tSize2 = tSm.UniformBlocks[i].Size;
                    string marker = pName != tName ? " *** MISMATCH" : (pSize2 != tSize2 ? " (size diff)" : "");
                    Console.WriteLine($"  UB[{i}]: prod={pName}({pSize2}) tf={tName}({tSize2}){marker}");
                }
                // Prod-only UBs
                for (int i = tSm.UniformBlocks.Count; i < pSm.UniformBlocks.Count; i++)
                    Console.WriteLine($"  UB[{i}]: prod-only {pSm.UniformBlocks.GetKey(i)}({pSm.UniformBlocks[i].Size})");

                // Compare Sampler dict order
                Console.WriteLine($"  Sampler count: prod={pSm.Samplers.Count} tf={tSm.Samplers.Count}");
                for (int i = 0; i < Math.Min(pSm.Samplers.Count, tSm.Samplers.Count); i++)
                {
                    string pName = pSm.Samplers.GetKey(i);
                    string tName = tSm.Samplers.GetKey(i);
                    string marker = pName != tName ? " *** MISMATCH" : "";
                    if (marker != "") Console.WriteLine($"  Smp[{i}]: prod={pName} tf={tName}{marker}");
                }

                // Compare Attribute dict order
                Console.WriteLine($"  Attr count: prod={pSm.Attributes.Count} tf={tSm.Attributes.Count}");
                for (int i = 0; i < Math.Max(pSm.Attributes.Count, tSm.Attributes.Count); i++)
                {
                    string pName = i < pSm.Attributes.Count ? pSm.Attributes.GetKey(i) : "(none)";
                    string tName = i < tSm.Attributes.Count ? tSm.Attributes.GetKey(i) : "(none)";
                    string marker = pName != tName ? " *** MISMATCH" : "";
                    if (marker != "") Console.WriteLine($"  Attr[{i}]: prod={pName} tf={tName}{marker}");
                }

                // Compare per-program binding locations for program 0
                if (pSm.Programs.Count > 0 && tSm.Programs.Count > 0)
                {
                    var pProg = pSm.Programs[0];
                    var tProg = tSm.Programs[0];
                    Console.WriteLine($"  Program[0] UB bindings:");
                    for (int i = 0; i < Math.Min(pProg.UniformBlockIndices.Count, tProg.UniformBlockIndices.Count); i++)
                    {
                        var pIdx = pProg.UniformBlockIndices[i];
                        var tIdx = tProg.UniformBlockIndices[i];
                        string pUbName = i < pSm.UniformBlocks.Count ? pSm.UniformBlocks.GetKey(i) : $"?{i}";
                        string tUbName = i < tSm.UniformBlocks.Count ? tSm.UniformBlocks.GetKey(i) : $"?{i}";
                        bool diff = pIdx.FragmentLocation != tIdx.FragmentLocation || pIdx.VertexLocation != tIdx.VertexLocation;
                        if (diff || pUbName != tUbName)
                            Console.WriteLine($"    [{i}] {pUbName}: prod=v{pIdx.VertexLocation}/f{pIdx.FragmentLocation} tf({tUbName})=v{tIdx.VertexLocation}/f{tIdx.FragmentLocation}");
                    }
                }
            }
            Console.WriteLine();

            // Scan ALL programs for gsys_user2 sampler binding
            Console.WriteLine("=== gsys_user2 Sampler Binding Scan ===");
            foreach (var pSm in pBfsha.ShaderModels.Values)
            {
                int user2Idx = -1;
                for (int i = 0; i < pSm.Samplers.Count; i++)
                    if (pSm.Samplers.GetKey(i) == "gsys_user2") { user2Idx = i; break; }
                if (user2Idx < 0) { Console.WriteLine($"  [{pSm.Name}] gsys_user2 NOT in sampler dict"); continue; }

                int bound = 0, unbound = 0;
                for (int p = 0; p < pSm.Programs.Count; p++)
                {
                    var si = pSm.Programs[p].SamplerIndices;
                    if (si != null && user2Idx < si.Count && si[user2Idx].FragmentLocation >= 0)
                        bound++;
                    else
                        unbound++;
                }
                Console.WriteLine($"  [{pSm.Name}] gsys_user2 (idx={user2Idx}): " +
                    $"PROD bound={bound}/{pSm.Programs.Count}, unbound={unbound}");

                // Same for testfire
                ShaderModel tSm2 = null;
                foreach (var t in tBfsha.ShaderModels.Values)
                    if (t.Name == pSm.Name) { tSm2 = t; break; }
                if (tSm2 != null)
                {
                    int tUser2Idx = -1;
                    for (int i = 0; i < tSm2.Samplers.Count; i++)
                        if (tSm2.Samplers.GetKey(i) == "gsys_user2") { tUser2Idx = i; break; }
                    if (tUser2Idx >= 0)
                    {
                        int tBound = 0, tUnbound = 0;
                        for (int p = 0; p < tSm2.Programs.Count; p++)
                        {
                            var si = tSm2.Programs[p].SamplerIndices;
                            if (si != null && tUser2Idx < si.Count && si[tUser2Idx].FragmentLocation >= 0)
                                tBound++;
                            else
                                tUnbound++;
                        }
                        Console.WriteLine($"  [{pSm.Name}] gsys_user2 (idx={tUser2Idx}): " +
                            $"TF bound={tBound}/{tSm2.Programs.Count}, unbound={tUnbound}");
                    }
                }
            }
            Console.WriteLine();

            // Correlate static options with gsys_user2 binding
            Console.WriteLine("=== Paint Option Correlation ===");
            foreach (var pSm in pBfsha.ShaderModels.Values)
            {
                int user2Idx = -1;
                for (int i = 0; i < pSm.Samplers.Count; i++)
                    if (pSm.Samplers.GetKey(i) == "gsys_user2") { user2Idx = i; break; }
                if (user2Idx < 0) continue;

                Console.WriteLine($"\n  [{pSm.Name}]");
                int keysPerProg = pSm.StaticKeyLength + pSm.DynamicKeyLength;

                for (int oi = 0; oi < pSm.StaticOptions.Count; oi++)
                {
                    var opt = pSm.StaticOptions.Values.ElementAt(oi);
                    string optName = pSm.StaticOptions.GetKey(oi);

                    var groups = new Dictionary<string, (int bound, int unbound)>();
                    for (int p = 0; p < pSm.Programs.Count; p++)
                    {
                        int keyVal = pSm.KeyTable[p * keysPerProg + opt.Bit32Index];
                        int choiceIdx = opt.GetChoiceIndex(keyVal);
                        string choice = choiceIdx >= 0 && choiceIdx < opt.Choices.Count
                            ? opt.Choices.GetKey(choiceIdx) : $"?{choiceIdx}";

                        var si = pSm.Programs[p].SamplerIndices;
                        bool bound = si != null && user2Idx < si.Count && si[user2Idx].FragmentLocation >= 0;

                        if (!groups.ContainsKey(choice)) groups[choice] = (0, 0);
                        var g = groups[choice];
                        groups[choice] = bound ? (g.bound + 1, g.unbound) : (g.bound, g.unbound + 1);
                    }

                    // Print options where bound/unbound distribution varies across values
                    // (even partial correlation helps identify paint option combo)
                    if (groups.Count > 1)
                    {
                        // Calculate bound ratio per group
                        bool hasDifferentRatios = false;
                        double? firstRatio = null;
                        foreach (var g in groups)
                        {
                            double total = g.Value.bound + g.Value.unbound;
                            double ratio = total > 0 ? g.Value.bound / total : 0;
                            if (firstRatio == null) firstRatio = ratio;
                            else if (Math.Abs(ratio - firstRatio.Value) > 0.2) hasDifferentRatios = true;
                        }
                        if (hasDifferentRatios)
                        {
                            Console.Write($"    {optName}: ");
                            foreach (var g in groups.OrderBy(g => g.Key))
                                Console.Write($"{g.Key}={g.Value.bound}b/{g.Value.unbound}u ");
                            Console.WriteLine();
                        }
                    }
                }
            }
            Console.WriteLine();
        }

        // Compare ShaderParam dictionaries between prod and testfire bfres
        Console.WriteLine("=== BFRES Material ShaderParam Comparison ===");
        {
            var pBfres = LoadBfres(args[0]);
            var tBfres = LoadBfres(args[1]);

            // Find shared materials
            foreach (var pModel in pBfres.Models.Values)
            foreach (var pMat in pModel.Materials.Values)
            {
                Material tMat = null;
                foreach (var tModel in tBfres.Models.Values)
                    if (tModel.Materials.ContainsKey(pMat.Name))
                    { tMat = tModel.Materials[pMat.Name]; break; }
                if (tMat == null) continue;

                Console.WriteLine($"\n  [{pMat.Name}]");
                Console.WriteLine($"    ShaderParamData: prod={pMat.ShaderParamData?.Length ?? 0} tf={tMat.ShaderParamData?.Length ?? 0}");
                Console.WriteLine($"    ShaderParams: prod={pMat.ShaderParams?.Count ?? 0} tf={tMat.ShaderParams?.Count ?? 0}");

                // List all shader params with offsets
                var allParams = new HashSet<string>();
                if (pMat.ShaderParams != null) foreach (var k in pMat.ShaderParams.Keys) allParams.Add(k);
                if (tMat.ShaderParams != null) foreach (var k in tMat.ShaderParams.Keys) allParams.Add(k);

                foreach (var paramName in allParams.OrderBy(x => x))
                {
                    bool inProd = pMat.ShaderParams?.ContainsKey(paramName) == true;
                    bool inTf = tMat.ShaderParams?.ContainsKey(paramName) == true;
                    if (inProd && inTf)
                    {
                        var pp = pMat.ShaderParams[paramName];
                        var tp = tMat.ShaderParams[paramName];
                        if (pp.DataOffset != tp.DataOffset || pp.Type != tp.Type)
                            Console.WriteLine($"    {paramName}: prod=off{pp.DataOffset}/{pp.Type} tf=off{tp.DataOffset}/{tp.Type} *** OFFSET DIFF");
                    }
                    else if (inProd)
                        Console.WriteLine($"    {paramName}: PROD-ONLY (off={pMat.ShaderParams[paramName].DataOffset})");
                    else
                        Console.WriteLine($"    {paramName}: TF-ONLY (off={tMat.ShaderParams[paramName].DataOffset})");
                }

                // RenderInfo comparison
                var allRI = new HashSet<string>();
                if (pMat.RenderInfos != null) foreach (var k in pMat.RenderInfos.Keys) allRI.Add(k);
                if (tMat.RenderInfos != null) foreach (var k in tMat.RenderInfos.Keys) allRI.Add(k);
                foreach (var riName in allRI.OrderBy(x => x))
                {
                    bool inProd = pMat.RenderInfos?.ContainsKey(riName) == true;
                    bool inTf = tMat.RenderInfos?.ContainsKey(riName) == true;
                    if (!inProd)
                        Console.WriteLine($"    RI:{riName}: TF-ONLY");
                    else if (!inTf)
                        Console.WriteLine($"    RI:{riName}: PROD-ONLY");
                }

                // ShaderAssign/ShaderOptions comparison
                if (pMat.ShaderAssign?.ShaderOptions != null && tMat.ShaderAssign?.ShaderOptions != null)
                {
                    int optDiffs = 0;
                    bool isGround = pMat.Name.Contains("Ground") || pMat.Name.Contains("Floor") || 
                                    pMat.Name.Contains("Map") || pMat.Name.Contains("Concrete");
                    foreach (var opt in pMat.ShaderAssign.ShaderOptions)
                    {
                        if (tMat.ShaderAssign.ShaderOptions.ContainsKey(opt.Key) &&
                            tMat.ShaderAssign.ShaderOptions[opt.Key] != opt.Value)
                        {
                            optDiffs++;
                            // Print blitz/paint options for ALL materials, other diffs only for ground
                            if (opt.Key.Contains("blitz") || opt.Key.Contains("paint") || opt.Key.Contains("map_paint"))
                                Console.WriteLine($"    OPT {opt.Key}: prod={opt.Value} tf={tMat.ShaderAssign.ShaderOptions[opt.Key]}");
                            else if (isGround && optDiffs <= 15)
                                Console.WriteLine($"    OPT {opt.Key}: prod={opt.Value} tf={tMat.ShaderAssign.ShaderOptions[opt.Key]}");
                        }
                    }
                    int pOnly = 0; foreach (var o in pMat.ShaderAssign.ShaderOptions) if (!tMat.ShaderAssign.ShaderOptions.ContainsKey(o.Key)) pOnly++;
                    int tOnly = 0; foreach (var o in tMat.ShaderAssign.ShaderOptions) if (!pMat.ShaderAssign.ShaderOptions.ContainsKey(o.Key)) tOnly++;
                    if (optDiffs > 0 || pOnly > 0 || tOnly > 0)
                        Console.WriteLine($"    ShaderOptions: {optDiffs} value diffs, {pOnly} prod-only, {tOnly} tf-only");
                }
            }
        }

        ResFile LoadBfres(string path) {
            var data = File.ReadAllBytes(path);
            var sarc = Oead.SarcRead(Oead.Yaz0Decompress(data));
            return new ResFile(new MemoryStream(sarc.First(kv => 
                kv.Key.EndsWith(".bfres", StringComparison.OrdinalIgnoreCase)).Value));
        }

        var prodBfres = LoadBfres(args[0]);
        var tfBfres = LoadBfres(args[1]);

        Console.WriteLine($"Prod models: {string.Join(", ", prodBfres.Models.Keys)}");
        Console.WriteLine($"Testfire models: {string.Join(", ", tfBfres.Models.Keys)}");

        // Build lookup for all materials
        var prodMats = new Dictionary<string, BfresLibrary.Material>();
        var tfMats = new Dictionary<string, BfresLibrary.Material>();
        foreach (var m in prodBfres.Models.Values) foreach (var mat in m.Materials.Values) prodMats[mat.Name] = mat;
        foreach (var m in tfBfres.Models.Values) foreach (var mat in m.Materials.Values) tfMats[mat.Name] = mat;

        // Compare shared materials
        var shared = prodMats.Keys.Intersect(tfMats.Keys).OrderBy(x => x).ToList();
        Console.WriteLine($"\nShared materials: {shared.Count}");
        Console.WriteLine($"Prod-only: {prodMats.Keys.Except(tfMats.Keys).Count()}");
        Console.WriteLine($"Testfire-only: {tfMats.Keys.Except(prodMats.Keys).Count()}");

        foreach (var name in shared)
        {
            var pm = prodMats[name];
            var tm = tfMats[name];

            var diffs = new List<string>();
            
            // Compare shader options
            var allOpts = new HashSet<string>();
            if (pm.ShaderAssign?.ShaderOptions != null) foreach (var o in pm.ShaderAssign.ShaderOptions) allOpts.Add(o.Key);
            if (tm.ShaderAssign?.ShaderOptions != null) foreach (var o in tm.ShaderAssign.ShaderOptions) allOpts.Add(o.Key);
            foreach (var opt in allOpts.OrderBy(x => x))
            {
                string pv = pm.ShaderAssign?.ShaderOptions?.ContainsKey(opt) == true ? pm.ShaderAssign.ShaderOptions[opt] : "(missing)";
                string tv = tm.ShaderAssign?.ShaderOptions?.ContainsKey(opt) == true ? tm.ShaderAssign.ShaderOptions[opt] : "(missing)";
                if (pv != tv)
                    diffs.Add($"  opt {opt}: prod={pv} tf={tv}");
            }

            // Compare render infos
            var allRi = new HashSet<string>();
            if (pm.RenderInfos != null) foreach (var r in pm.RenderInfos) allRi.Add(r.Key);
            if (tm.RenderInfos != null) foreach (var r in tm.RenderInfos) allRi.Add(r.Key);
            foreach (var ri in allRi.OrderBy(x => x))
            {
                string pv = "(missing)", tv = "(missing)";
                if (pm.RenderInfos != null) foreach (var r in pm.RenderInfos) if (r.Key == ri) {
                    var vals = r.Value.GetValueStrings(); pv = vals.Length > 0 ? string.Join(",", vals) : "(empty)"; break; }
                if (tm.RenderInfos != null) foreach (var r in tm.RenderInfos) if (r.Key == ri) {
                    var vals = r.Value.GetValueStrings(); tv = vals.Length > 0 ? string.Join(",", vals) : "(empty)"; break; }
                if (pv != tv)
                    diffs.Add($"  ri  {ri}: prod={pv} tf={tv}");
            }

            // Compare sampler assigns
            if (pm.ShaderAssign?.SamplerAssigns != null && tm.ShaderAssign?.SamplerAssigns != null) {
                var allS = new HashSet<string>();
                foreach (var s in pm.ShaderAssign.SamplerAssigns) allS.Add(s.Key);
                foreach (var s in tm.ShaderAssign.SamplerAssigns) allS.Add(s.Key);
                foreach (var s in allS.OrderBy(x => x)) {
                    string pv = pm.ShaderAssign.SamplerAssigns.ContainsKey(s) ? pm.ShaderAssign.SamplerAssigns[s] : "(missing)";
                    string tv = tm.ShaderAssign.SamplerAssigns.ContainsKey(s) ? tm.ShaderAssign.SamplerAssigns[s] : "(missing)";
                    if (pv != tv) diffs.Add($"  smp {s}: prod={pv} tf={tv}");
                }
            }

            // Compare ShaderParams (uniform values)
            if (pm.ShaderParams != null && tm.ShaderParams != null) {
                var allP = new HashSet<string>();
                foreach (var p in pm.ShaderParams) allP.Add(p.Key);
                foreach (var p in tm.ShaderParams) allP.Add(p.Key);
                int paramDiffs = 0;
                foreach (var pName in allP.OrderBy(x => x)) {
                    bool inProd = pm.ShaderParams.ContainsKey(pName);
                    bool inTf = tm.ShaderParams.ContainsKey(pName);
                    if (!inProd || !inTf) {
                        diffs.Add($"  prm {pName}: {(inProd ? "prod-only" : "tf-only")}");
                        paramDiffs++;
                    } else {
                        var pp = pm.ShaderParams[pName];
                        var tp = tm.ShaderParams[pName];
                        string pvStr = pp.DataValue?.ToString() ?? "(null)";
                        string tvStr = tp.DataValue?.ToString() ?? "(null)";
                        if (pvStr != tvStr) {
                            diffs.Add($"  prm {pName}: prod={pvStr} tf={tvStr}");
                            paramDiffs++;
                        }
                    }
                }
                if (paramDiffs > 0) diffs.Add($"  (total {paramDiffs} param diffs)");
            }

            if (diffs.Count > 0)
            {
                Console.WriteLine($"\n{name} ({pm.ShaderAssign?.ShadingModelName} vs {tm.ShaderAssign?.ShadingModelName}): {diffs.Count} diffs");
                foreach (var d in diffs) Console.WriteLine(d);
            }
        }

        // Show testfire-only materials
        Console.WriteLine("\n--- Testfire-only materials ---");
        foreach (var name in tfMats.Keys.Except(prodMats.Keys).OrderBy(x => x))
        {
            var tm = tfMats[name];
            Console.WriteLine($"  {name} ({tm.ShaderAssign?.ShadingModelName})");
        }
        return 0;
    }
    /// <summary>
    /// Decompile prod and testfire paint-enabled fragment shaders for comparison.
    /// Outputs GLSL files to /tmp/paint_shaders/ for offset analysis.
    /// Usage: --decompile-paint prod.szs testfire.szs
    /// </summary>
    static int RunDecompilePaint(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: --decompile-paint prod.szs testfire.szs");
            return 1;
        }

        string outputDir = "/tmp/paint_shaders";
        Directory.CreateDirectory(outputDir);

        // Load bfsha from SZS
        BfshaFile LoadBfsha(string path)
        {
            var data = File.ReadAllBytes(path);
            var sarc = Oead.SarcRead(Oead.Yaz0Decompress(data));
            var bfshaData = sarc.First(kv => kv.Key.EndsWith(".bfsha", StringComparison.OrdinalIgnoreCase)).Value;
            return new BfshaFile(new MemoryStream(bfshaData));
        }

        var prodBfsha = LoadBfsha(args[0]);
        var tfBfsha = LoadBfsha(args[1]);

        // Downgrade prod for fair comparison
        prodBfsha = BfshaConverter.Downgrade(prodBfsha);

        Console.WriteLine("=== Decompiling Paint Shaders ===\n");

        foreach (var pSm in prodBfsha.ShaderModels.Values)
        {
            // Find gsys_user2 sampler index
            int user2SmpIdx = -1;
            for (int i = 0; i < pSm.Samplers.Count; i++)
                if (pSm.Samplers.GetKey(i) == "gsys_user2") { user2SmpIdx = i; break; }
            if (user2SmpIdx < 0) continue;

            // Find testfire counterpart
            ShaderModel tSm = null;
            foreach (var t in tfBfsha.ShaderModels.Values)
                if (t.Name == pSm.Name) { tSm = t; break; }
            if (tSm == null) continue;

            int tUser2SmpIdx = -1;
            for (int i = 0; i < tSm.Samplers.Count; i++)
                if (tSm.Samplers.GetKey(i) == "gsys_user2") { tUser2SmpIdx = i; break; }

            Console.WriteLine($"[{pSm.Name}]");

            // Find a paint-enabled prod program (gsys_user2 fragment bound)
            int prodPaintProg = -1;
            for (int p = 0; p < pSm.Programs.Count; p++)
            {
                var si = pSm.Programs[p].SamplerIndices;
                if (si != null && user2SmpIdx < si.Count && si[user2SmpIdx].FragmentLocation >= 0)
                {
                    prodPaintProg = p;
                    break;
                }
            }

            if (prodPaintProg < 0)
            {
                Console.WriteLine("  No paint-enabled prod program found");
                continue;
            }

            Console.WriteLine($"  Prod paint program: {prodPaintProg}");

            // Build option signature for matching
            var sharedOptionNames = new List<string>();
            for (int i = 0; i < tSm.StaticOptions.Count; i++)
            {
                string name = tSm.StaticOptions.GetKey(i);
                if (pSm.StaticOptions.ContainsKey(name))
                    sharedOptionNames.Add(name);
            }

            // Get option values for the prod paint program
            int pKeysPerProg = pSm.StaticKeyLength + pSm.DynamicKeyLength;
            Console.WriteLine("  Prod program options:");
            foreach (var optName in sharedOptionNames)
            {
                var opt = pSm.StaticOptions[optName];
                int keyVal = pSm.KeyTable[prodPaintProg * pKeysPerProg + opt.Bit32Index];
                int choiceIdx = opt.GetChoiceIndex(keyVal);
                string choice = choiceIdx >= 0 && choiceIdx < opt.Choices.Count
                    ? opt.Choices.GetKey(choiceIdx) : $"?{choiceIdx}";
                if (choice != opt.DefaultChoice)
                    Console.WriteLine($"    {optName} = {choice} (default={opt.DefaultChoice})");
            }

            // Find matching testfire program
            int tfPaintProg = -1;
            if (tUser2SmpIdx >= 0)
            {
                // Try to find a testfire program that also binds gsys_user2
                for (int p = 0; p < tSm.Programs.Count; p++)
                {
                    var si = tSm.Programs[p].SamplerIndices;
                    if (si != null && tUser2SmpIdx < si.Count && si[tUser2SmpIdx].FragmentLocation >= 0)
                    {
                        tfPaintProg = p;
                        break;
                    }
                }
            }

            Console.WriteLine($"  Testfire paint program: {tfPaintProg}");

            // Decompile prod fragment shader
            var prodVar = pSm.GetVariation(prodPaintProg);
            if (prodVar?.BinaryProgram?.FragmentShader != null)
            {
                string prodGlsl = ShaderLibrary.ShaderExtract.GetCode(
                    prodVar.BinaryProgram.FragmentShader,
                    prodVar.BinaryProgram.FragmentShaderReflection);
                string prodPath = Path.Combine(outputDir, $"{pSm.Name}_prod_frag_p{prodPaintProg}.glsl");
                File.WriteAllText(prodPath, prodGlsl);
                Console.WriteLine($"  Prod frag → {prodPath} ({prodGlsl.Length} chars)");

                // Also decompile prod vertex shader
                if (prodVar.BinaryProgram.VertexShader != null)
                {
                    string prodVertGlsl = ShaderLibrary.ShaderExtract.GetCode(
                        prodVar.BinaryProgram.VertexShader,
                        prodVar.BinaryProgram.VertexShaderReflection);
                    string prodVertPath = Path.Combine(outputDir, $"{pSm.Name}_prod_vert_p{prodPaintProg}.glsl");
                    File.WriteAllText(prodVertPath, prodVertGlsl);
                    Console.WriteLine($"  Prod vert → {prodVertPath} ({prodVertGlsl.Length} chars)");
                }
            }

            // Decompile testfire fragment shader
            if (tfPaintProg >= 0)
            {
                var tfVar = tSm.GetVariation(tfPaintProg);
                if (tfVar?.BinaryProgram?.FragmentShader != null)
                {
                    string tfGlsl = ShaderLibrary.ShaderExtract.GetCode(
                        tfVar.BinaryProgram.FragmentShader,
                        tfVar.BinaryProgram.FragmentShaderReflection);
                    string tfPath = Path.Combine(outputDir, $"{pSm.Name}_testfire_frag_p{tfPaintProg}.glsl");
                    File.WriteAllText(tfPath, tfGlsl);
                    Console.WriteLine($"  TF frag → {tfPath} ({tfGlsl.Length} chars)");

                    if (tfVar.BinaryProgram.VertexShader != null)
                    {
                        string tfVertGlsl = ShaderLibrary.ShaderExtract.GetCode(
                            tfVar.BinaryProgram.VertexShader,
                            tfVar.BinaryProgram.VertexShaderReflection);
                        string tfVertPath = Path.Combine(outputDir, $"{pSm.Name}_testfire_vert_p{tfPaintProg}.glsl");
                        File.WriteAllText(tfVertPath, tfVertGlsl);
                        Console.WriteLine($"  TF vert → {tfVertPath} ({tfVertGlsl.Length} chars)");
                    }
                }
            }

            Console.WriteLine();
        }

        Console.WriteLine($"Shaders written to {outputDir}");
        return 0;
    }

    private static void HandleDeliTextures(ResFile convertedBfres, string inputDir)
    {
        string deliTexturesPath = Path.Combine(inputDir, "DeliTextures.Nin_NX_NVN.szs");
        if (!File.Exists(deliTexturesPath))
        {
            // Try one level up if not found in inputDir
            deliTexturesPath = Path.Combine(Path.GetDirectoryName(inputDir) ?? ".", "DeliTextures.Nin_NX_NVN.szs");
        }

        if (File.Exists(deliTexturesPath))
        {
            Console.WriteLine($"  [Deli] Loading textures from: {deliTexturesPath}");
            try
            {
                byte[] deliDecompressed = Oead.Yaz0DecompressFile(deliTexturesPath);
                var deliSarcFiles = Oead.SarcRead(deliDecompressed);
                var deliBfresEntry = deliSarcFiles.FirstOrDefault(f => Path.GetExtension(f.Key).Equals(".bfres", StringComparison.OrdinalIgnoreCase));
                
                if (deliBfresEntry.Value != null)
                {
                    var deliBfres = new ResFile(new MemoryStream(deliBfresEntry.Value));
                    
                    // Transfer Textures
                    int addedTex = 0;
                    foreach (var tex in deliBfres.Textures.Values)
                    {
                        if (!convertedBfres.Textures.ContainsKey(tex.Name))
                        {
                            convertedBfres.Textures.Add(tex.Name, tex);
                            addedTex++;
                        }
                    }

                    // Transfer ExternalFiles (crucial for BNTX data)
                    int addedExt = 0;
                    foreach (var ext in deliBfres.ExternalFiles)
                    {
                        if (!convertedBfres.ExternalFiles.ContainsKey(ext.Key))
                        {
                            convertedBfres.ExternalFiles.Add(ext.Key, ext.Value);
                            addedExt++;
                        }
                    }
                    Console.WriteLine($"    [Deli] Added {addedTex} textures and {addedExt} external files from DeliTextures.szs");
                }
                else
                {
                    Console.WriteLine("    [Deli] WARNING: DeliTextures.szs contains no .bfres file");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [Deli] ERROR: Failed to load DeliTextures: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("  [Deli] WARNING: DeliTextures.Nin_NX_NVN.szs not found");
        }
    }

    static int RunRoundTripTest(string szsPath)
    {
        Console.WriteLine($"=== BfresLibrary V5 Round-Trip Test ===");
        Console.WriteLine($"Input: {Path.GetFileName(szsPath)}");
        
        byte[] dec = Oead.Yaz0DecompressFile(szsPath);
        var sarc = Oead.SarcRead(dec);
        byte[] origBfres = null;
        string bfresName = null;
        foreach (var f in sarc)
        {
            if (f.Key.EndsWith(".bfres")) { origBfres = f.Value; bfresName = f.Key; break; }
        }
        if (origBfres == null) { Console.Error.WriteLine("No .bfres found in SZS"); return 1; }
        
        Console.WriteLine($"BFRES: {bfresName}, {origBfres.Length} bytes, ver={origBfres[10]}.{origBfres[8]}.{origBfres[9]}.{origBfres[11]}");
        
        // Load
        BufferInfo.BufferOffset = 0;
        var resFile = new ResFile(new MemoryStream(origBfres));
        Console.WriteLine($"Loaded: {resFile.Models.Count} models, name='{resFile.Name}'");
        foreach (var m in resFile.Models)
            Console.WriteLine($"  '{m.Key}': {m.Value.Shapes.Count}sh {m.Value.Materials.Count}mat {m.Value.VertexBuffers.Count}vb {m.Value.Skeleton.Bones.Count}bone");
        
        // Save
        BufferInfo.BufferOffset = 0;
        var ms = new MemoryStream();
        resFile.Save(ms);
        byte[] savedBfres = ms.ToArray();
        Console.WriteLine($"\nSaved: {savedBfres.Length} bytes (diff: {savedBfres.Length - origBfres.Length})");
        
        // Also dump saved to /tmp for analysis
        File.WriteAllBytes("/tmp/roundtrip_saved.bfres", savedBfres);
        File.WriteAllBytes("/tmp/roundtrip_orig.bfres", origBfres);
        Console.WriteLine("Wrote /tmp/roundtrip_orig.bfres and /tmp/roundtrip_saved.bfres");
        
        // Compare section-by-section
        var magics = new[] { "FMDL", "FSHP", "FMAT", "FVTX", "FSKL" };
        var strides = new[] { 120, 112, 184, 96, -1 };
        
        for (int mi = 0; mi < magics.Length; mi++)
        {
            var mb = System.Text.Encoding.ASCII.GetBytes(magics[mi]);
            var origOff = FindAllMagic(origBfres, mb);
            var savedOff = FindAllMagic(savedBfres, mb);
            
            string countSt = origOff.Count == savedOff.Count ? "✓" : "*** COUNT MISMATCH ***";
            Console.WriteLine($"\n{magics[mi]}: orig={origOff.Count} saved={savedOff.Count} {countSt}");
            
            if (strides[mi] > 0 && origOff.Count >= 2 && savedOff.Count >= 2)
            {
                // Check if entries within the same model are consecutive
                int os = origOff[1] - origOff[0], ss = savedOff[1] - savedOff[0];
                if (os < 1000 && ss < 1000) // Only if they're in the same model
                {
                    Console.WriteLine($"  Stride: orig={os} saved={ss} {(os == ss ? "✓" : "*** STRIDE MISMATCH ***")}");
                }
            }

            // Compare first entry byte-by-byte
            int sz = strides[mi] > 0 ? strides[mi] : 64;
            int cnt = Math.Min(2, Math.Min(origOff.Count, savedOff.Count));
            for (int i = 0; i < cnt; i++)
            {
                int diffs = 0;
                for (int j = 0; j < sz && origOff[i]+j < origBfres.Length && savedOff[i]+j < savedBfres.Length; j++)
                    if (origBfres[origOff[i]+j] != savedBfres[savedOff[i]+j]) diffs++;
                
                if (diffs > 0)
                {
                    Console.WriteLine($"  [{i}]: {diffs} diffs");
                    int shown = 0;
                    for (int j = 0; j < sz && shown < 8 && origOff[i]+j < origBfres.Length && savedOff[i]+j < savedBfres.Length; j++)
                    {
                        if (origBfres[origOff[i]+j] != savedBfres[savedOff[i]+j])
                        {
                            Console.WriteLine($"    +0x{j:X2}: orig=0x{origBfres[origOff[i]+j]:X2} saved=0x{savedBfres[savedOff[i]+j]:X2}");
                            shown++;
                        }
                    }
                }
                else Console.WriteLine($"  [{i}]: identical ✓");
            }
        }
        
        // RLT comparison
        uint origRlt = BitConverter.ToUInt32(origBfres, 0x18);
        uint savedRlt = BitConverter.ToUInt32(savedBfres, 0x18);
        Console.WriteLine($"\nRLT: orig=0x{origRlt:X} saved=0x{savedRlt:X}");
        
        for (int s = 0; s < 5; s++)
        {
            int ob = (int)origRlt + 16 + s * 24;
            int sb = (int)savedRlt + 16 + s * 24;
            if (ob + 24 > origBfres.Length || sb + 24 > savedBfres.Length) break;
            
            uint oo = BitConverter.ToUInt32(origBfres, ob + 8), osz = BitConverter.ToUInt32(origBfres, ob + 12);
            uint oei = BitConverter.ToUInt32(origBfres, ob + 16), oec = BitConverter.ToUInt32(origBfres, ob + 20);
            uint so = BitConverter.ToUInt32(savedBfres, sb + 8), ssz = BitConverter.ToUInt32(savedBfres, sb + 12);
            uint sei = BitConverter.ToUInt32(savedBfres, sb + 16), sec = BitConverter.ToUInt32(savedBfres, sb + 20);
            
            bool match = oo == so && osz == ssz && oei == sei && oec == sec;
            Console.WriteLine($"  Sec{s}: {(match ? "✓" : "***DIFF***")}  orig(off=0x{oo:X} sz=0x{osz:X} ei={oei} ent={oec})  saved(off=0x{so:X} sz=0x{ssz:X} ei={sei} ent={sec})");
        }
        
        Console.WriteLine("\nDone.");
        return 0;
    }
    
    static List<int> FindAllMagic(byte[] data, byte[] magic)
    {
        var results = new List<int>();
        for (int i = 0; i <= data.Length - magic.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < magic.Length; j++) { if (data[i+j] != magic[j]) { ok = false; break; } }
            if (ok) results.Add(i);
        }
        return results;
    }
}
