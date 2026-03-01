using BfresLibrary;
using BfresLibrary.Switch;

namespace HammerheadConverter;

/// <summary>
/// Converts bfres files from prod to testfire format.
/// 
/// Uses the testfire bfres as the structural template. Transfers model,
/// material, and animation data from prod. BfresLibrary's Save method
/// dynamically handles version-specific serialization (V9 vs V10 header
/// layout, material format, shape bounding format, etc.) based on the
/// version fields — so the testfire's version determines correct output.
/// </summary>
public static class BfresConverter
{
    public static ResFile Convert(ResFile prodBfres, ResFile testfireBfres)
    {
        Console.WriteLine("\n=== BFRES Conversion ===");
        Console.WriteLine($"  Prod version: " +
            $"Major={prodBfres.VersionMajor}, Major2={prodBfres.VersionMajor2}, " +
            $"Minor={prodBfres.VersionMinor}, Minor2={prodBfres.VersionMinor2}");
        Console.WriteLine($"  Testfire version: " +
            $"Major={testfireBfres.VersionMajor}, Major2={testfireBfres.VersionMajor2}, " +
            $"Minor={testfireBfres.VersionMinor}, Minor2={testfireBfres.VersionMinor2}");

        Console.WriteLine($"  Prod: {prodBfres.Models.Count} models, " +
            $"{prodBfres.ExternalFiles.Count} external files");
        Console.WriteLine($"  Testfire: {testfireBfres.Models.Count} models, " +
            $"{testfireBfres.ExternalFiles.Count} external files");

        // Strategy: Use the testfire bfres as our structural base.
        // Transfer prod model data into it with version adaptation.
        // The testfire's version fields ensure the saver writes the correct layout.
        ResFile output = testfireBfres;

        // Transfer models from prod to testfire structure
        TransferModels(prodBfres, output);

        // Transfer animations
        TransferAnimations(prodBfres, output);

        // Transfer external files (bntx textures, etc.)
        TransferExternalFiles(prodBfres, output);

        // Transfer memory pool and buffer data (required for vertex/index buffers in modern ResFiles)
        output.MemoryPool = prodBfres.MemoryPool;
        output.BufferInfo = prodBfres.BufferInfo;
        output.Alignment = prodBfres.Alignment;
        output.ExternalFlag = prodBfres.ExternalFlag;
        output.Flag = prodBfres.Flag;
        output.BlockOffset = prodBfres.BlockOffset;

        // Transfer metadata
        output.Name = prodBfres.Name;

        Console.WriteLine($"  Output: {output.Models.Count} models, " +
            $"Major2={output.VersionMajor2}, Minor={output.VersionMinor}");

        // CRITICAL: Reset the static BufferOffset in BufferInfo.
        // BfresLibrary uses this static field during Save to track global buffer position.
        // When batching, it must be reset to 0 for each new file.
        BufferInfo.BufferOffset = 0;

        return output;
    }

    private static void TransferModels(ResFile prod, ResFile output)
    {
        output.Models.Clear();

        foreach (var model in prod.Models)
        {
            Console.WriteLine($"    Model '{model.Key}': " +
                $"{model.Value.Shapes.Count} shapes, {model.Value.Materials.Count} materials");

            output.Models.Add(model.Key, model.Value);

            // Adapt materials for the target version
            foreach (var mat in model.Value.Materials.Values)
                AdaptMaterial(mat, prod, output);

            // Adapt shapes for the target version
            foreach (var shape in model.Value.Shapes.Values)
                AdaptShape(shape, prod, output);

            // Adapt skeleton
            AdaptSkeleton(model.Value.Skeleton, prod, output);
        }
    }

    private static void AdaptMaterial(Material mat, ResFile prod, ResFile output)
    {
        bool prodIsV10 = prod.VersionMajor2 >= 10;
        bool outputIsV10 = output.VersionMajor2 >= 10;

        if (prodIsV10 && !outputIsV10)
        {
            // V10 → older: clear V10-specific data, standard ShaderAssign already populated
            mat.ShaderInfoV10 = null;
            Console.WriteLine($"      Material '{mat.Name}': V10→older, cleared V10 data");
        }
        else if (!prodIsV10 && outputIsV10)
        {
            // older → V10: prepare V10 shader info
            MaterialParserV10.PrepareSave(mat);
            Console.WriteLine($"      Material '{mat.Name}': older→V10, prepared V10 data");
        }

        // Ensure required fields are initialized
        if (mat.VolatileFlags == null) mat.VolatileFlags = new byte[0];
        if (mat.ShaderParamData == null) mat.ShaderParamData = new byte[0];
    }

    private static void AdaptSkeleton(Skeleton skeleton, ResFile prod, ResFile output)
    {
        // V8+ might have different flags or scaling modes.
        // Testfire (V5) expects standard Maya scaling and EulerXYZ/Quaternion.
        
        // Ensure BoneList is consistent and indices are valid short values
        foreach (var bone in skeleton.Bones.Values)
        {
            // Sanitize flags for older versions if needed
            // (Most bone flags are compatible, but billboard types might shift)
        }
    }

    /// <summary>
    /// Adapts all materials in the bfres to match available testfire shader programs.
    /// Strategy: For materials with known-good options from any testfire bfres, use those directly.
    /// Only fall back to hybrid matching for materials not found in any testfire bfres.
    /// </summary>
    public static void AdaptMaterialsForTestfire(
        ResFile bfres,
        Dictionary<string, (string ShaderModel, Dictionary<string, string> Options)> knownGoodMaterials,
        List<BfshaHybridBuilder.MatchResult> matchResults,
        int testfireMaterialBlockSize = 912)
    {
        Console.WriteLine("\n=== Adapting materials for testfire bfsha ===");
        Console.WriteLine($"    Known-good material pool: {knownGoodMaterials.Count} materials from all testfire bfres");

        // Build lookup from material name to match result
        var matchLookup = new Dictionary<string, BfshaHybridBuilder.MatchResult>();
        foreach (var match in matchResults)
            matchLookup[match.MaterialName] = match;

        foreach (var model in bfres.Models.Values)
        {
            foreach (var mat in model.Materials.Values)
            {
                if (mat.ShaderAssign?.ShaderOptions == null)
                    continue;

                var currentOpts = mat.ShaderAssign.ShaderOptions;

                // Strategy 1: Use testfire material options directly (known-good)
                if (knownGoodMaterials.ContainsKey(mat.Name))
                {
                    var tfOpts = knownGoodMaterials[mat.Name].Options;

                    // Replace all options with testfire values
                    var keysToRemove = new List<string>();
                    foreach (var opt in currentOpts)
                    {
                        if (!tfOpts.ContainsKey(opt.Key))
                            keysToRemove.Add(opt.Key);
                    }
                    foreach (var key in keysToRemove)
                        currentOpts.Remove(key);
                    foreach (var opt in tfOpts)
                    {
                        if (currentOpts.ContainsKey(opt.Key))
                            currentOpts[opt.Key] = opt.Value;
                        else
                            currentOpts.Add(opt.Key, opt.Value);
                    }

                    Console.WriteLine($"    '{mat.Name}': using testfire material options (known-good)");
                }
                // Strategy 2: Use hybrid match result for prod-only materials
                else if (matchLookup.ContainsKey(mat.Name))
                {
                    var match = matchLookup[mat.Name];
                    var adaptedOpts = match.AdaptedOptions;

                    var keysToRemove = new List<string>();
                    foreach (var opt in currentOpts)
                    {
                        if (!adaptedOpts.ContainsKey(opt.Key))
                            keysToRemove.Add(opt.Key);
                    }
                    foreach (var key in keysToRemove)
                        currentOpts.Remove(key);
                    foreach (var opt in adaptedOpts)
                    {
                        if (currentOpts.ContainsKey(opt.Key))
                            currentOpts[opt.Key] = opt.Value;
                        else
                            currentOpts.Add(opt.Key, opt.Value);
                    }

                    string matchType = match.IsExactMatch ? "exact" : $"fallback ({match.DifferingOptions} bits)";
                    Console.WriteLine($"    '{mat.Name}' ({match.ShaderModelName}): " +
                        $"program {match.ProgramIndex} [{matchType}] (prod-only)");
                }

                // Trim ShaderParamData if it exceeds testfire size
                if (mat.ShaderParamData != null && mat.ShaderParamData.Length > testfireMaterialBlockSize)
                {
                    Console.WriteLine($"    '{mat.Name}': trimming ShaderParamData " +
                        $"{mat.ShaderParamData.Length} → {testfireMaterialBlockSize} bytes");
                    var trimmed = new byte[testfireMaterialBlockSize];
                    Array.Copy(mat.ShaderParamData, trimmed, testfireMaterialBlockSize);
                    mat.ShaderParamData = trimmed;
                }
            }
        }
    }

    private static void AdaptShape(Shape shape, ResFile prod, ResFile output)
    {
        bool prodIsV10 = prod.VersionMajor2 >= 10;
        bool outputIsV10 = output.VersionMajor2 >= 10;

        if (prodIsV10 && !outputIsV10)
        {
            // V10 uses BoundingRadiusList (Vector4F per bounding) instead of RadiusArray
            if (shape.BoundingRadiusList != null && shape.BoundingRadiusList.Count > 0 &&
                (shape.RadiusArray == null || shape.RadiusArray.Count == 0))
            {
                // Extract max radius from bounding list
                float maxRadius = 0;
                foreach (var br in shape.BoundingRadiusList)
                    maxRadius = Math.Max(maxRadius, br.W);
                shape.RadiusArray = new List<float> { maxRadius };
            }
        }
        // V10 output: ShapeParser.Write already handles BoundingRadiusList regeneration

        // Ensure required collections
        if (shape.SkinBoneIndices == null) shape.SkinBoneIndices = new List<ushort>();
        if (shape.SubMeshBoundings == null) shape.SubMeshBoundings = new List<Bounding>();
        if (shape.SubMeshBoundingNodes == null) shape.SubMeshBoundingNodes = new List<BoundingNode>();
    }

    private static void TransferAnimations(ResFile prod, ResFile output)
    {
        int total = 0;

        if (prod.SkeletalAnims.Count > 0)
        {
            output.SkeletalAnims = prod.SkeletalAnims;
            total += prod.SkeletalAnims.Count;
        }

        // Material animations — use the categorized public properties
        if (prod.ShaderParamAnims.Count > 0)
        {
            output.ShaderParamAnims = prod.ShaderParamAnims;
            total += prod.ShaderParamAnims.Count;
        }
        if (prod.TexPatternAnims.Count > 0)
        {
            output.TexPatternAnims = prod.TexPatternAnims;
            total += prod.TexPatternAnims.Count;
        }
        if (prod.ColorAnims.Count > 0)
        {
            output.ColorAnims = prod.ColorAnims;
            total += prod.ColorAnims.Count;
        }
        if (prod.TexSrtAnims.Count > 0)
        {
            output.TexSrtAnims = prod.TexSrtAnims;
            total += prod.TexSrtAnims.Count;
        }
        if (prod.MatVisibilityAnims.Count > 0)
        {
            output.MatVisibilityAnims = prod.MatVisibilityAnims;
            total += prod.MatVisibilityAnims.Count;
        }

        if (prod.BoneVisibilityAnims.Count > 0)
        {
            output.BoneVisibilityAnims = prod.BoneVisibilityAnims;
            total += prod.BoneVisibilityAnims.Count;
        }

        if (prod.ShapeAnims.Count > 0)
        {
            output.ShapeAnims = prod.ShapeAnims;
            total += prod.ShapeAnims.Count;
        }

        if (prod.SceneAnims.Count > 0)
        {
            output.SceneAnims = prod.SceneAnims;
            total += prod.SceneAnims.Count;
        }

        Console.WriteLine($"    Animations: {total} total transferred");
    }

    private static void TransferExternalFiles(ResFile prod, ResFile output)
    {
        if (prod.ExternalFiles.Count > 0)
        {
            output.ExternalFiles = prod.ExternalFiles;
            output.Textures = prod.Textures;
            Console.WriteLine($"    External files: {prod.ExternalFiles.Count} transferred");
        }
    }
}
