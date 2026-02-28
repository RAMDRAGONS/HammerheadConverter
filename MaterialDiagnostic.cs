using ShaderLibrary;
using BfresLibrary;

namespace HammerheadConverter;

/// <summary>
/// Diagnostic tool to trace material option modifications and find why
/// some prod materials fail GetProgramIndex against their own prod bfsha.
/// </summary>
public static class MaterialDiagnostic
{
    public static void Run(string szsPath, string[] sharedBfshaPaths)
    {
        Console.WriteLine("=== Material Diagnostic ===");
        Console.WriteLine($"Source: {szsPath}");

        // Load the raw prod SZS
        byte[] szsData = File.ReadAllBytes(szsPath);
        byte[] sarcData = Oead.Yaz0Decompress(szsData);
        var sarcFiles = Oead.SarcRead(sarcData);

        // Load testfire version to compare options
        string baseName = Path.GetFileName(szsPath);
        string tfPath = Path.Combine("shadict", baseName);
        if (File.Exists(tfPath))
        {
            var tfSarcFiles = Oead.SarcRead(Oead.Yaz0Decompress(File.ReadAllBytes(tfPath)));
            var tfBfshaEntry = tfSarcFiles.FirstOrDefault(kv =>
                Path.GetExtension(kv.Key).Equals(".bfsha", StringComparison.OrdinalIgnoreCase));
            var prodBfshaEntry2 = sarcFiles.FirstOrDefault(kv =>
                Path.GetExtension(kv.Key).Equals(".bfsha", StringComparison.OrdinalIgnoreCase));
            if (tfBfshaEntry.Key != null && prodBfshaEntry2.Key != null)
            {
                var tfBfsha = new BfshaFile(new MemoryStream(tfBfshaEntry.Value));
                var prodBfsha2 = new BfshaFile(new MemoryStream(prodBfshaEntry2.Value));
                Console.WriteLine("\n--- Prod-only vs Testfire-only options ---");
                foreach (var prodSm in prodBfsha2.ShaderModels.Values)
                {
                    ShaderModel tfSm = null;
                    foreach (var t in tfBfsha.ShaderModels.Values)
                        if (t.Name == prodSm.Name) { tfSm = t; break; }
                    if (tfSm == null) continue;

                    Console.WriteLine($"\n  {prodSm.Name}: prod={prodSm.StaticOptions.Count}, testfire={tfSm.StaticOptions.Count}");
                    Console.WriteLine("  PROD-ONLY:");
                    for (int i = 0; i < prodSm.StaticOptions.Count; i++)
                    {
                        var key = prodSm.StaticOptions.GetKey(i);
                        if (!tfSm.StaticOptions.ContainsKey(key))
                        {
                            var opt = prodSm.StaticOptions[i];
                            Console.WriteLine($"    {key} (default={opt.DefaultChoice}, choices={string.Join("/", opt.Choices.Keys)})");
                        }
                    }
                    Console.WriteLine("  TESTFIRE-ONLY:");
                    for (int i = 0; i < tfSm.StaticOptions.Count; i++)
                    {
                        var key = tfSm.StaticOptions.GetKey(i);
                        if (!prodSm.StaticOptions.ContainsKey(key))
                            Console.WriteLine($"    {key} (default={tfSm.StaticOptions[i].DefaultChoice})");
                    }
                }
            }
        }




        // Load prod bfres (raw, unmodified)
        var bfresEntry = sarcFiles.FirstOrDefault(kv =>
            Path.GetExtension(kv.Key).Equals(".bfres", StringComparison.OrdinalIgnoreCase));
        if (bfresEntry.Key == null) { Console.WriteLine("  No bfres found"); return; }
        var prodBfres = new ResFile(new MemoryStream(bfresEntry.Value));

        // Load prod bfsha (raw, unmodified)
        var bfshaEntry = sarcFiles.FirstOrDefault(kv =>
            Path.GetExtension(kv.Key).Equals(".bfsha", StringComparison.OrdinalIgnoreCase));
        if (bfshaEntry.Key == null) { Console.WriteLine("  No bfsha found"); return; }
        var prodBfsha = new BfshaFile(new MemoryStream(bfshaEntry.Value));

        var failingMats = new[] { "Bike00", "Mirror", "DVWaterBasic", "MapGround", "DVTreeSet", "Glasspatern", "GroundAsphaltEdge", "Logo00", "MonsteraLeaf", "NewFence01", "DVLeaves" };

        // Test each material against the RAW prod bfsha (no modifications)
        Console.WriteLine("\n--- RAW prod bfsha test (no modifications) ---");
        foreach (var model in prodBfres.Models.Values)
        {
            foreach (var mat in model.Materials.Values)
            {
                if (!failingMats.Contains(mat.Name)) continue;

                var sa = mat.ShaderAssign;
                if (sa?.ShadingModelName == null || sa.ShaderOptions == null) continue;

                Console.WriteLine($"\n  Material: {mat.Name} (model: {model.Name})");
                Console.WriteLine($"    ShaderModel: {sa.ShadingModelName}");
                Console.WriteLine($"    Options count: {sa.ShaderOptions.Count}");

                // Find matching shader model
                ShaderModel sm = null;
                foreach (var s in prodBfsha.ShaderModels.Values)
                    if (s.Name == sa.ShadingModelName) { sm = s; break; }

                if (sm == null)
                {
                    Console.WriteLine($"    ⚠ Shader model '{sa.ShadingModelName}' NOT FOUND in prod bfsha");
                    Console.WriteLine($"    Available models: {string.Join(", ", prodBfsha.ShaderModels.Values.Select(s => s.Name))}");
                    continue;
                }

                Console.WriteLine($"    Shader model found: {sm.StaticOptions.Count} static opts, {sm.Programs.Count} programs");

                var matOpts = new Dictionary<string, string>();
                foreach (var opt in sa.ShaderOptions) matOpts[opt.Key] = opt.Value;

                int progIdx = -1;
                try { progIdx = sm.GetProgramIndex(matOpts); } catch (Exception ex) {
                    Console.WriteLine($"    GetProgramIndex exception: {ex.Message}");
                }

                Console.WriteLine($"    GetProgramIndex: {progIdx}");

                if (progIdx < 0)
                {
                    // Show differing options
                    Console.WriteLine($"    Options not in shader model:");
                    foreach (var opt in sa.ShaderOptions)
                    {
                        if (!sm.StaticOptions.ContainsKey(opt.Key) && !sm.DynamicOptions.ContainsKey(opt.Key))
                            Console.WriteLine($"      {opt.Key} = {opt.Value}");
                    }
                    Console.WriteLine($"    Missing static options (in SM but not in material):");
                    int missing = 0;
                    for (int i = 0; i < sm.StaticOptions.Count; i++)
                    {
                        var optName = sm.StaticOptions.GetKey(i);
                        if (!sa.ShaderOptions.ContainsKey(optName))
                        {
                            Console.WriteLine($"      {optName} (default: {sm.StaticOptions[i].DefaultChoice})");
                            missing++;
                        }
                    }
                    Console.WriteLine($"    Total missing: {missing}");
                }
            }
        }

        // Check shared bfsha files
        Console.WriteLine("\n--- Checking shared bfsha/shader files ---");
        foreach (var path in sharedBfshaPaths)
        {
            if (!File.Exists(path)) { Console.WriteLine($"  {Path.GetFileName(path)}: NOT FOUND"); continue; }

            byte[] data = File.ReadAllBytes(path);
            byte[] decompressed;
            try { decompressed = Oead.Yaz0Decompress(data); }
            catch { Console.WriteLine($"  {Path.GetFileName(path)}: not Yaz0 compressed, trying raw"); decompressed = data; }

            Dictionary<string, byte[]> files;
            try { files = Oead.SarcRead(decompressed); }
            catch { Console.WriteLine($"  {Path.GetFileName(path)}: not a SARC"); continue; }

            // List ALL file types
            var extGroups = files.Keys.GroupBy(k => Path.GetExtension(k).ToLower()).OrderBy(g => g.Key);
            Console.WriteLine($"  {Path.GetFileName(path)}: {files.Count} files");
            foreach (var g in extGroups)
                Console.WriteLine($"    {g.Key}: {g.Count()} files ({string.Join(", ", g.Take(3).Select(k => Path.GetFileName(k)))}{(g.Count() > 3 ? "..." : "")})");

            // List bfsha contents
            var bfshaKeys = files.Keys.Where(k => k.EndsWith(".bfsha", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var key in bfshaKeys)
            {
                try
                {
                    var bfsha = new BfshaFile(new MemoryStream(files[key]));
                    Console.WriteLine($"    {key}:");
                    foreach (var sm in bfsha.ShaderModels.Values)
                        Console.WriteLine($"      {sm.Name}: {sm.Programs.Count} programs, {sm.StaticOptions.Count} static opts");
                }
                catch (Exception ex) { Console.WriteLine($"    {key}: ERROR: {ex.Message}"); }
            }
        }

        // Diagnostic: for a failing material, find which program is CLOSEST
        Console.WriteLine("\n--- FindClosestProgram diagnostic for failing materials ---");
        foreach (var model in prodBfres.Models.Values)
            foreach (var mat in model.Materials.Values)
            {
                if (!failingMats.Contains(mat.Name)) continue;
                var sa = mat.ShaderAssign;
                if (sa?.ShadingModelName == null || sa.ShaderOptions == null) continue;

                ShaderModel sm = null;
                foreach (var s in prodBfsha.ShaderModels.Values)
                    if (s.Name == sa.ShadingModelName) { sm = s; break; }
                if (sm == null) continue;

                var matOpts = new Dictionary<string, string>();
                foreach (var opt in sa.ShaderOptions) matOpts[opt.Key] = opt.Value;

                // Check each program: count differing options
                int bestDist = int.MaxValue;
                int bestProg = -1;
                var bestDiffs = new List<string>();
                for (int pi = 0; pi < sm.Programs.Count; pi++)
                {
                    int dist = 0;
                    var diffs = new List<string>();
                    int numKeysPerProgram = sm.StaticKeyLength + sm.DynamicKeyLength;
                    int baseIndex = numKeysPerProgram * pi;

                    for (int j = 0; j < sm.StaticOptions.Count; j++)
                    {
                        var option = sm.StaticOptions[j];
                        if (!matOpts.ContainsKey(option.Name)) continue;

                        int choiceIndex = option.GetChoiceIndex(sm.KeyTable[baseIndex + option.Bit32Index]);
                        if (choiceIndex < 0 || choiceIndex >= option.Choices.Count) continue;
                        string progChoice = option.Choices.GetKey(choiceIndex);

                        if (matOpts[option.Name] != progChoice)
                        {
                            dist++;
                            diffs.Add($"{option.Name}: {matOpts[option.Name]} → {progChoice}");
                        }
                    }

                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestProg = pi;
                        bestDiffs = diffs;
                    }
                }

                Console.WriteLine($"\n  {mat.Name}: closest program={bestProg}, distance={bestDist}");
                foreach (var d in bestDiffs.Take(10))
                    Console.WriteLine($"    {d}");
                if (bestDiffs.Count > 10)
                    Console.WriteLine($"    ... and {bestDiffs.Count - 10} more");
            }
    }
}
