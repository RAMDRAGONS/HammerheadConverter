using ShaderLibrary;
using static ShaderLibrary.BnshFile;

namespace HammerheadConverter;

/// <summary>
/// Compares prod and testfire shader programs from shared SZS files
/// to extract the systematic differences that need to be accounted for
/// when transplanting prod shader code into testfire format.
/// </summary>
public static class ShaderComparer
{
    /// <summary>
    /// Compare shader reflection data between prod and testfire versions of a shared SZS file.
    /// Dumps: shader model names, uniform block layouts, sampler bindings, attribute locations,
    /// and detects any systematic differences.
    /// </summary>
    public static void CompareSharedFile(string prodSzsPath, string testfireSzsPath)
    {
        Console.WriteLine($"\n{new string('=', 80)}");
        Console.WriteLine($"SHADER COMPARISON");
        Console.WriteLine($"  Prod:     {Path.GetFileName(prodSzsPath)}");
        Console.WriteLine($"  Testfire: {Path.GetFileName(testfireSzsPath)}");
        Console.WriteLine(new string('=', 80));

        // Extract bfsha from both
        var prodBfsha = ExtractBfsha(prodSzsPath, "prod");
        var testfireBfsha = ExtractBfsha(testfireSzsPath, "testfire");

        if (prodBfsha == null || testfireBfsha == null)
        {
            Console.WriteLine("  ERROR: Could not extract bfsha from one or both SZS files.");
            return;
        }

        Console.WriteLine($"\n  Prod bfsha:     {prodBfsha.ShaderModels.Count} shader models");
        Console.WriteLine($"  Testfire bfsha: {testfireBfsha.ShaderModels.Count} shader models");

        // Compare each shader model
        foreach (var prodSm in prodBfsha.ShaderModels.Values)
        {
            ShaderModel testfireSm = null;
            foreach (var sm in testfireBfsha.ShaderModels.Values)
            {
                // Try exact name match first, then _np suffix match 
                if (sm.Name == prodSm.Name ||
                    sm.Name == prodSm.Name + "_np" ||
                    sm.Name + "_np" == prodSm.Name)
                {
                    testfireSm = sm;
                    break;
                }
            }

            if (testfireSm == null)
            {
                Console.WriteLine($"\n  [{prodSm.Name}] — NO TESTFIRE MATCH");
                continue;
            }

            CompareShaderModels(prodSm, testfireSm);
        }
    }

    static void CompareShaderModels(ShaderModel prod, ShaderModel testfire)
    {
        Console.WriteLine($"\n  ─── {prod.Name} (prod) vs {testfire.Name} (testfire) ───");

        // Version/format info
        Console.WriteLine($"    Programs: prod={prod.Programs.Count}, testfire={testfire.Programs.Count}");
        Console.WriteLine($"    StaticKeyLength: prod={prod.StaticKeyLength}, testfire={testfire.StaticKeyLength}");
        Console.WriteLine($"    DynamicKeyLength: prod={prod.DynamicKeyLength}, testfire={testfire.DynamicKeyLength}");

        // Compare uniform blocks
        Console.WriteLine($"\n    UNIFORM BLOCKS (prod={prod.UniformBlocks.Count}, testfire={testfire.UniformBlocks.Count}):");
        var allUbNames = new HashSet<string>();
        foreach (var k in prod.UniformBlocks.Keys) allUbNames.Add(k);
        foreach (var k in testfire.UniformBlocks.Keys) allUbNames.Add(k);
        
        foreach (var name in allUbNames.OrderBy(x => x))
        {
            bool inProd = prod.UniformBlocks.ContainsKey(name);
            bool inTestfire = testfire.UniformBlocks.ContainsKey(name);
            
            if (inProd && inTestfire)
            {
                var pb = prod.UniformBlocks[name];
                var tb = testfire.UniformBlocks[name];
                string diff = (pb.Index != tb.Index || pb.Type != tb.Type || pb.Size != tb.Size)
                    ? " *** DIFF ***" : "";
                Console.WriteLine($"      {name}: prod(idx={pb.Index}, type={pb.Type}, size={pb.Size}) " +
                    $"testfire(idx={tb.Index}, type={tb.Type}, size={tb.Size}){diff}");
            }
            else if (inProd)
                Console.WriteLine($"      {name}: PROD ONLY (idx={prod.UniformBlocks[name].Index}, type={prod.UniformBlocks[name].Type})");
            else
                Console.WriteLine($"      {name}: TESTFIRE ONLY (idx={testfire.UniformBlocks[name].Index}, type={testfire.UniformBlocks[name].Type})");
        }

        // Compare samplers
        Console.WriteLine($"\n    SAMPLERS (prod={prod.Samplers.Count}, testfire={testfire.Samplers.Count}):");
        var allSamplerNames = new HashSet<string>();
        foreach (var k in prod.Samplers.Keys) allSamplerNames.Add(k);
        foreach (var k in testfire.Samplers.Keys) allSamplerNames.Add(k);
        
        foreach (var name in allSamplerNames.OrderBy(x => x))
        {
            bool inProd = prod.Samplers.ContainsKey(name);
            bool inTestfire = testfire.Samplers.ContainsKey(name);
            
            if (inProd && inTestfire)
            {
                var ps = prod.Samplers[name];
                var ts = testfire.Samplers[name];
                string diff = ps.Index != ts.Index ? " *** DIFF ***" : "";
                Console.WriteLine($"      {name}: prod(idx={ps.Index}) testfire(idx={ts.Index}){diff}");
            }
            else if (inProd)
                Console.WriteLine($"      {name}: PROD ONLY (idx={prod.Samplers[name].Index})");
            else
                Console.WriteLine($"      {name}: TESTFIRE ONLY (idx={testfire.Samplers[name].Index})");
        }

        // Compare attributes
        Console.WriteLine($"\n    ATTRIBUTES (prod={prod.Attributes.Count}, testfire={testfire.Attributes.Count}):");
        var allAttrNames = new HashSet<string>();
        foreach (var k in prod.Attributes.Keys) allAttrNames.Add(k);
        foreach (var k in testfire.Attributes.Keys) allAttrNames.Add(k);
        
        foreach (var name in allAttrNames.OrderBy(x => x))
        {
            bool inProd = prod.Attributes.ContainsKey(name);
            bool inTestfire = testfire.Attributes.ContainsKey(name);
            
            if (inProd && inTestfire)
            {
                var pa = prod.Attributes[name];
                var ta = testfire.Attributes[name];
                string diff = (pa.Index != ta.Index || pa.Location != ta.Location) ? " *** DIFF ***" : "";
                Console.WriteLine($"      {name}: prod(idx={pa.Index}, loc={pa.Location}) " +
                    $"testfire(idx={ta.Index}, loc={ta.Location}){diff}");
            }
            else if (inProd)
                Console.WriteLine($"      {name}: PROD ONLY (idx={prod.Attributes[name].Index})");
            else
                Console.WriteLine($"      {name}: TESTFIRE ONLY (idx={testfire.Attributes[name].Index})");
        }

        // Compare static options (just count mismatches)
        int sharedOptions = 0, prodOnlyOptions = 0, testfireOnlyOptions = 0;
        foreach (var key in prod.StaticOptions.Keys)
            if (testfire.StaticOptions.ContainsKey(key)) sharedOptions++;
            else prodOnlyOptions++;
        foreach (var key in testfire.StaticOptions.Keys)
            if (!prod.StaticOptions.ContainsKey(key)) testfireOnlyOptions++;
        
        Console.WriteLine($"\n    STATIC OPTIONS: shared={sharedOptions}, prodOnly={prodOnlyOptions}, testfireOnly={testfireOnlyOptions}");

        // Compare block indices 
        if (prod.BlockIndices != null && testfire.BlockIndices != null)
        {
            Console.WriteLine($"\n    BLOCK INDICES:");
            Console.WriteLine($"      prod:     [{string.Join(", ", prod.BlockIndices)}]");
            Console.WriteLine($"      testfire: [{string.Join(", ", testfire.BlockIndices)}]");
        }

        // Compare symbol data
        if (prod.SymbolData != null && testfire.SymbolData != null)
        {
            Console.WriteLine($"\n    SYMBOL DATA:");
            Console.WriteLine($"      Samplers:       prod={prod.SymbolData.Samplers.Count}, testfire={testfire.SymbolData.Samplers.Count}");
            Console.WriteLine($"      UniformBlocks:  prod={prod.SymbolData.UniformBlocks.Count}, testfire={testfire.SymbolData.UniformBlocks.Count}");
            Console.WriteLine($"      StorageBuffers: prod={prod.SymbolData.StorageBuffers.Count}, testfire={testfire.SymbolData.StorageBuffers.Count}");
        }

        // Compare a sample program's reflection data (first shared program)
        CompareFirstSharedProgram(prod, testfire);
    }

    static void CompareFirstSharedProgram(ShaderModel prod, ShaderModel testfire)
    {
        // Find the first program in prod, look up its options, see if testfire has an equivalent
        if (prod.Programs.Count == 0 || testfire.Programs.Count == 0) return;

        // Pick program 0 from each and compare reflection
        Console.WriteLine($"\n    PROGRAM REFLECTION (program 0 of each):");

        var prodVar = prod.GetVariation(0);
        var testfireVar = testfire.GetVariation(0);

        if (prodVar == null || testfireVar == null)
        {
            Console.WriteLine($"      Could not load variation for program 0");
            return;
        }

        var prodBin = prodVar.BinaryProgram;
        var testfireBin = testfireVar.BinaryProgram;

        // Compare vertex shader reflection
        CompareReflection("    VERTEX", prodBin.VertexShaderReflection, testfireBin.VertexShaderReflection);
        CompareReflection("    FRAGMENT", prodBin.FragmentShaderReflection, testfireBin.FragmentShaderReflection);

        // Compare program binding indices
        var prodProg = prod.Programs[0];
        var testfireProg = testfire.Programs[0];

        Console.WriteLine($"\n    PROGRAM 0 BINDINGS:");
        Console.WriteLine($"      UsedAttributeFlags: prod=0x{prodProg.UsedAttributeFlags:X8}, testfire=0x{testfireProg.UsedAttributeFlags:X8}");
        Console.WriteLine($"      Flags: prod=0x{prodProg.Flags:X}, testfire=0x{testfireProg.Flags:X}");

        // Uniform block binding comparison
        int maxUb = Math.Max(prodProg.UniformBlockIndices.Count, testfireProg.UniformBlockIndices.Count);
        for (int i = 0; i < maxUb; i++)
        {
            string prodUb = i < prodProg.UniformBlockIndices.Count
                ? $"v={prodProg.UniformBlockIndices[i].VertexLocation},f={prodProg.UniformBlockIndices[i].FragmentLocation}"
                : "N/A";
            string tfUb = i < testfireProg.UniformBlockIndices.Count
                ? $"v={testfireProg.UniformBlockIndices[i].VertexLocation},f={testfireProg.UniformBlockIndices[i].FragmentLocation}"
                : "N/A";
            string ubName = i < prod.UniformBlocks.Count ? prod.UniformBlocks.GetKey(i) : $"[{i}]";
            string diff = prodUb != tfUb ? " *** DIFF ***" : "";
            Console.WriteLine($"        UB[{i}] {ubName}: prod({prodUb}) testfire({tfUb}){diff}");
        }

        // Sampler binding comparison
        int maxSamp = Math.Max(prodProg.SamplerIndices.Count, testfireProg.SamplerIndices.Count);
        for (int i = 0; i < maxSamp; i++)
        {
            string prodS = i < prodProg.SamplerIndices.Count
                ? $"v={prodProg.SamplerIndices[i].VertexLocation},f={prodProg.SamplerIndices[i].FragmentLocation}"
                : "N/A";
            string tfS = i < testfireProg.SamplerIndices.Count
                ? $"v={testfireProg.SamplerIndices[i].VertexLocation},f={testfireProg.SamplerIndices[i].FragmentLocation}"
                : "N/A";
            string sName = i < prod.Samplers.Count ? prod.Samplers.GetKey(i) : $"[{i}]";
            string diff = prodS != tfS ? " *** DIFF ***" : "";
            Console.WriteLine($"        Samp[{i}] {sName}: prod({prodS}) testfire({tfS}){diff}");
        }
    }

    static void CompareReflection(string label, ShaderReflectionData prod, ShaderReflectionData testfire)
    {
        if (prod == null && testfire == null) return;
        if (prod == null) { Console.WriteLine($"    {label}: PROD NULL"); return; }
        if (testfire == null) { Console.WriteLine($"    {label}: TESTFIRE NULL"); return; }

        Console.WriteLine($"\n    {label} REFLECTION:");
        Console.WriteLine($"      Inputs:         prod={prod.Inputs.Count}, testfire={testfire.Inputs.Count}");
        Console.WriteLine($"      Outputs:        prod={prod.Outputs.Count}, testfire={testfire.Outputs.Count}");
        Console.WriteLine($"      Samplers:       prod={prod.Samplers.Count}, testfire={testfire.Samplers.Count}");
        Console.WriteLine($"      UniformBuffers: prod={prod.UniformBuffers.Count}, testfire={testfire.UniformBuffers.Count}");
    }

    /// <summary>
    /// Try decompiling program 0 from both prod and testfire, and dump the decompiled GLSL
    /// to files for manual comparison. Also prints key similarities/differences.
    /// </summary>
    public static void DecompileAndCompare(string prodSzsPath, string testfireSzsPath, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        var prodBfsha = ExtractBfsha(prodSzsPath, "prod");
        var testfireBfsha = ExtractBfsha(testfireSzsPath, "testfire");
        if (prodBfsha == null || testfireBfsha == null) return;

        string baseName = Path.GetFileNameWithoutExtension(prodSzsPath).Replace(".Nin_NX_NVN", "");

        foreach (var prodSm in prodBfsha.ShaderModels.Values)
        {
            ShaderModel testfireSm = null;
            foreach (var sm in testfireBfsha.ShaderModels.Values)
            {
                if (sm.Name == prodSm.Name || sm.Name == prodSm.Name + "_np" || sm.Name + "_np" == prodSm.Name)
                {
                    testfireSm = sm;
                    break;
                }
            }
            if (testfireSm == null) continue;
            if (prodSm.Programs.Count == 0 || testfireSm.Programs.Count == 0) continue;

            var prodVar = prodSm.GetVariation(0);
            var testfireVar = testfireSm.GetVariation(0);
            if (prodVar?.BinaryProgram == null || testfireVar?.BinaryProgram == null) continue;

            string smSafeName = prodSm.Name.Replace("/", "_");

            // Decompile vertex shaders
            try
            {
                if (prodVar.BinaryProgram.VertexShader?.ByteCode != null)
                {
                    string prodVertGlsl = ShaderExtract.GetCode(prodVar.BinaryProgram.VertexShader,
                        prodVar.BinaryProgram.VertexShaderReflection);
                    File.WriteAllText(Path.Combine(outputDir, $"{baseName}_{smSafeName}_prod.vert"), prodVertGlsl);
                }
                if (testfireVar.BinaryProgram.VertexShader?.ByteCode != null)
                {
                    string tfVertGlsl = ShaderExtract.GetCode(testfireVar.BinaryProgram.VertexShader,
                        testfireVar.BinaryProgram.VertexShaderReflection);
                    File.WriteAllText(Path.Combine(outputDir, $"{baseName}_{smSafeName}_testfire.vert"), tfVertGlsl);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Vertex decompile error for {prodSm.Name}: {ex.Message}");
            }

            // Decompile fragment shaders
            try
            {
                if (prodVar.BinaryProgram.FragmentShader?.ByteCode != null)
                {
                    string prodFragGlsl = ShaderExtract.GetCode(prodVar.BinaryProgram.FragmentShader,
                        prodVar.BinaryProgram.FragmentShaderReflection);
                    File.WriteAllText(Path.Combine(outputDir, $"{baseName}_{smSafeName}_prod.frag"), prodFragGlsl);
                }
                if (testfireVar.BinaryProgram.FragmentShader?.ByteCode != null)
                {
                    string tfFragGlsl = ShaderExtract.GetCode(testfireVar.BinaryProgram.FragmentShader,
                        testfireVar.BinaryProgram.FragmentShaderReflection);
                    File.WriteAllText(Path.Combine(outputDir, $"{baseName}_{smSafeName}_testfire.frag"), tfFragGlsl);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Fragment decompile error for {prodSm.Name}: {ex.Message}");
            }

            Console.WriteLine($"    Exported GLSL for {prodSm.Name} → {baseName}_{smSafeName}_*.{{vert,frag}}");
        }
    }

    public static BfshaFile ExtractBfsha(string szsPath, string label)
    {
        try
        {
            // Decompress Yaz0
            byte[] szsData = File.ReadAllBytes(szsPath);
            byte[] sarcData = Oead.Yaz0Decompress(szsData);

            // Read SARC
            var sarcFiles = Oead.SarcRead(sarcData);

            // Find bfsha
            foreach (var kv in sarcFiles)
            {
                if (Path.GetExtension(kv.Key).Equals(".bfsha", StringComparison.OrdinalIgnoreCase))
                {
                    var bfsha = new BfshaFile(new MemoryStream(kv.Value));
                    Console.WriteLine($"  Loaded {label} bfsha: {kv.Key} ({kv.Value.Length} bytes, {bfsha.ShaderModels.Count} models)");
                    return bfsha;
                }
            }

            Console.WriteLine($"  No bfsha found in {label} SZS");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Error loading {label} bfsha: {ex.Message}");
            return null;
        }
    }
}
