using ShaderLibrary;

namespace HammerheadConverter;

/// <summary>
/// Converts bfsha files for use with testfire by downgrading V7→V5 format.
/// 
/// Approach: Load the prod bfsha (V7), strip V7-only fields, change version to V5,
/// and save. The saver automatically uses V5 code paths based on the version number.
/// All prod metadata (uniform blocks, samplers, options, programs) stays intact
/// since it's consistent with the prod bnsh (compiled shader binary).
/// The bnsh passes through as-is — both V5 and V7 target NX_NVN.
/// </summary>
public static class BfshaConverter
{
    /// <summary>
    /// Downgrade a prod V7 bfsha to V5 format.
    /// </summary>
    public static BfshaFile Downgrade(BfshaFile prodBfsha)
    {
        Console.WriteLine("=== BFSHA Downgrade V7→V5 ===");
        Console.WriteLine($"  Input version: Major={prodBfsha.BinHeader.VersionMajor}, " +
            $"Minor={prodBfsha.BinHeader.VersionMinor}, Micro={prodBfsha.BinHeader.VersionMicro}");
        Console.WriteLine($"  Shader models: {prodBfsha.ShaderModels.Count}");

        if (prodBfsha.BinHeader.VersionMajor <= 5)
        {
            Console.WriteLine("  Already V5 or older — no downgrade needed.");
            return prodBfsha;
        }

        // --- Set version to V5 ---
        prodBfsha.BinHeader.VersionMajor = 5;
        prodBfsha.BinHeader.VersionMinor = 0;
        prodBfsha.BinHeader.VersionMicro = 0;

        // --- Strip V7+ only fields from each ShaderModel ---
        for (int i = 0; i < prodBfsha.ShaderModels.Count; i++)
        {
            var model = prodBfsha.ShaderModels[i];
            StripV7Fields(model);
        }

        Console.WriteLine($"  Output version: Major=5, Minor=0, Micro=0");
        return prodBfsha;
    }

    private static void StripV7Fields(ShaderModel model)
    {
        int storageCount = model.StorageBuffers?.Count ?? 0;

        // Clear StorageBuffers — V5 doesn't have this section
        model.StorageBuffers = new ResDict<BfshaStorageBuffer>();

        // Clear V8+ Images 
        model.Images = new ResDict<BfshaImageBuffer>();

        // Clear V8+ UnknownIndices2
        model.UnknownIndices2 = null;

        // Strip StorageBufferIndices and ImageIndices from each program
        foreach (var prog in model.Programs)
        {
            prog.StorageBufferIndices = new List<ShaderIndexHeader>();
            prog.ImageIndices = new List<ShaderIndexHeader>();
        }

        // Strip StorageBuffer symbols if present
        if (model.SymbolData != null)
        {
            model.SymbolData.StorageBuffers = new List<SymbolData.SymbolEntry>();
        }

        // Remap BlockIndices from V7 to V5 format.
        // V7: [shape, skeleton, option, 0]
        // V5: [material, shape, skeleton, option]
        // The material block index was removed in V7 (derivable from Type field).
        // We need to find it and prepend it for V5.
        if (model.BlockIndices == null || model.BlockIndices.Length != 4)
            model.BlockIndices = new byte[4];

        byte materialBlockIndex = 0;
        foreach (var ub in model.UniformBlocks.Values)
        {
            if (ub.Type == BfshaUniformBlock.BlockType.Material)
            {
                materialBlockIndex = ub.Index;
                break;
            }
        }

        // V7 BlockIndices: [0]=shape, [1]=skeleton, [2]=option, [3]=0
        byte v7Shape = model.BlockIndices[0];
        byte v7Skeleton = model.BlockIndices[1];
        byte v7Option = model.BlockIndices[2];

        // V5 BlockIndices: [0]=material, [1]=shape, [2]=skeleton, [3]=option
        model.BlockIndices[0] = materialBlockIndex;
        model.BlockIndices[1] = v7Shape;
        model.BlockIndices[2] = v7Skeleton;
        model.BlockIndices[3] = v7Option;

        Console.WriteLine($"    [{model.Name}] stripped {storageCount} storage buffers, " +
            $"{model.Programs.Count} programs updated, " +
            $"BlockIndices V7[{v7Shape},{v7Skeleton},{v7Option},0] → V5[{materialBlockIndex},{v7Shape},{v7Skeleton},{v7Option}]");
    }
}
