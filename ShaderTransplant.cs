using ShaderLibrary;

namespace HammerheadConverter;

/// <summary>
/// Adapts a prod bfsha (already V5-downgraded) for testfire by modifying shader model
/// metadata to match testfire expectations. All prod GPU bytecode and program bindings
/// are preserved as-is since both target Tegra X1 NVN.
///
/// Systematic differences discovered from comparing Fld_Ditch01, Obj_AirBall, Player00:
///   1. Sampler rename: gsys_depth_buffer → gsys_normalized_linear_depth (idx=27)
///   2. Uniform block sizes: gsys_material 1040→912, gsys_user0 896→832, gsys_user2 9024→41024
///   3. Static options: 13 prod-only options removed from dict, bits zeroed in key table
///   4. Block indices: already handled by BfshaConverter V7→V5 downgrade
/// </summary>
public static class ShaderTransplant
{
    // Sampler renames: prod name → testfire name
    private static readonly Dictionary<string, string> SamplerRenames = new()
    {
        { "gsys_depth_buffer", "gsys_normalized_linear_depth" }
    };

    // Uniform block size overrides: name → testfire size
    private static readonly Dictionary<string, int> TestfireBlockSizes = new()
    {
        { "gsys_material", 912 },
        { "gsys_user0", 832 },
        { "gsys_user2", 41024 }
    };

    /// <summary>
    /// Adapt a prod bfsha (already V5-downgraded) for testfire.
    /// Modifies shader model metadata in-place. Preserves all GPU bytecode and bindings.
    /// </summary>
    public static void AdaptForTestfire(BfshaFile prodBfsha, BfshaFile testfireBfsha = null)
    {
        Console.WriteLine("=== Shader Transplant: Adapting prod bfsha for testfire ===");

        for (int i = 0; i < prodBfsha.ShaderModels.Count; i++)
        {
            var sm = prodBfsha.ShaderModels.Values.ElementAt(i);
            Console.WriteLine($"  [{sm.Name}] {sm.Programs.Count} programs");

            // Find matching testfire shader model for reference
            ShaderModel refSm = null;
            if (testfireBfsha != null)
            {
                foreach (var tsm in testfireBfsha.ShaderModels.Values)
                {
                    if (tsm.Name == sm.Name || tsm.Name == sm.Name + "_np" || sm.Name == tsm.Name + "_np")
                    {
                        refSm = tsm;
                        break;
                    }
                }
            }

            AdaptSamplers(sm);
            AdaptUniformBlockSizes(sm);
            AdaptStaticOptions(sm, refSm);
            
            // Copy paint-critical metadata flags from testfire if available.
            // Unknown2 (prod=5, tf=0) and UnknownIndices2 (prod=[], tf=[0,0,0,0])
            // may signal paint capability to the runtime.
            // NOTE: Do NOT copy BlockIndices — that breaks per-program binding for unswapped programs.
            if (refSm != null)
            {
                sm.Unknown2 = refSm.Unknown2;
                if (refSm.UnknownIndices2 != null)
                    sm.UnknownIndices2 = (byte[])refSm.UnknownIndices2.Clone();

                Console.WriteLine($"    Adapted: samplers, UB sizes, options, Unknown2={sm.Unknown2}, UnknownIdx2=[{string.Join(",", sm.UnknownIndices2 ?? Array.Empty<byte>())}]");
            }
            else
            {
                Console.WriteLine($"    Adapted: samplers, UB sizes (no refSm for metadata)");
            }
        }

        Console.WriteLine("=== Shader Transplant complete ===");
    }

    private static void AdaptScalarMetadata(ShaderModel sm, ShaderModel refSm)
    {
        if (refSm == null) return;

        sm.Unknown2 = refSm.Unknown2;
        if (refSm.BlockIndices != null)
            sm.BlockIndices = (byte[])refSm.BlockIndices.Clone();
        if (refSm.UnknownIndices2 != null)
            sm.UnknownIndices2 = (byte[])refSm.UnknownIndices2.Clone();
        
        sm.MaxVSRingItemSize = refSm.MaxVSRingItemSize;
        sm.MaxRingItemSize = refSm.MaxRingItemSize;
        
        Console.WriteLine($"    Scalar metadata: Unknown2={sm.Unknown2}, BlockIndices=[{string.Join(",", sm.BlockIndices)}]");
    }

    /// <summary>
    /// Rename prod-only samplers to testfire equivalents.
    /// </summary>
    private static void AdaptSamplers(ShaderModel sm)
    {
        foreach (var rename in SamplerRenames)
        {
            if (sm.Samplers.ContainsKey(rename.Key))
            {
                var newSamplers = new ResDict<BfshaSampler>();
                foreach (var kv in sm.Samplers)
                {
                    string key = kv.Key == rename.Key ? rename.Value : kv.Key;
                    newSamplers.Add(key, kv.Value);
                }
                sm.Samplers = newSamplers;
                Console.WriteLine($"    Renamed sampler: {rename.Key} → {rename.Value}");
            }
        }

        // Also rename in SymbolData if present
        if (sm.SymbolData?.Samplers != null)
        {
            foreach (var rename in SamplerRenames)
            {
                for (int i = 0; i < sm.SymbolData.Samplers.Count; i++)
                {
                    if (sm.SymbolData.Samplers[i].Name1 == rename.Key)
                    {
                        var entry = sm.SymbolData.Samplers[i];
                        entry.Name1 = rename.Value;
                        entry.Name2 = rename.Value;
                        entry.Name3 = rename.Value;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Set uniform block sizes to match testfire expectations.
    /// Paint-related UBs (gsys_user0, gsys_user2) use EXACT testfire size
    /// so the runtime fills paint data in the correct layout.
    /// gsys_material uses MAX(prod, testfire) since prod bytecode reads
    /// material params at prod-sized offsets.
    /// </summary>
    private static void AdaptUniformBlockSizes(ShaderModel sm)
    {
        // Paint-related UBs that must use exact testfire size for runtime compat
        var paintUBs = new HashSet<string> { "gsys_user0", "gsys_user2" };

        foreach (var blockSize in TestfireBlockSizes)
        {
            if (sm.UniformBlocks.ContainsKey(blockSize.Key))
            {
                var ub = sm.UniformBlocks[blockSize.Key];
                int origSize = ub.Size;
                
                if (paintUBs.Contains(blockSize.Key))
                {
                    // Paint UBs: exact testfire size for correct runtime data layout
                    ub.header.Size = (ushort)blockSize.Value;
                    if (origSize != ub.header.Size)
                        Console.WriteLine($"    UB {blockSize.Key}: {origSize} → {ub.header.Size} (testfire exact)");
                }
                else
                {
                    // Other UBs: MAX to avoid out-of-bounds from prod bytecode
                    ub.header.Size = (ushort)Math.Max(origSize, blockSize.Value);
                    if (origSize != ub.header.Size)
                        Console.WriteLine($"    UB {blockSize.Key}: {origSize} → {ub.header.Size} (max)");
                }
            }
        }
    }

    /// <summary>
    /// Align the attribute dict to match testfire's layout.
    /// Testfire adds _b0 (binormal) at index 3, shifting subsequent attributes.
    /// This is critical for full program swap: testfire vertex shaders use
    /// UsedAttributeFlags to reference attributes by index. If the index→name
    /// mapping differs, the runtime binds wrong vertex data.
    /// The runtime binds attributes by NAME, so missing _b0 in vertex buffers
    /// just means the shader reads zeros (no crash, just zero binormal).
    /// </summary>
    private static void AdaptAttributes(ShaderModel sm, ShaderModel refSm)
    {
        if (refSm == null) return;

        // Only adapt if attribute counts differ (testfire has _b0, prod doesn't)
        if (sm.Attributes.Count == refSm.Attributes.Count) return;

        int origCount = sm.Attributes.Count;

        // Build new attribute dict matching testfire's order
        var newAttrs = new ResDict<BfshaAttribute>();
        for (int i = 0; i < refSm.Attributes.Count; i++)
        {
            string name = refSm.Attributes.GetKey(i);
            if (sm.Attributes.ContainsKey(name))
                newAttrs.Add(name, sm.Attributes[name]);
            else
                newAttrs.Add(name, new BfshaAttribute {
                    Index = refSm.Attributes[i].Index,
                    Location = refSm.Attributes[i].Location
                });
        }
        sm.Attributes = newAttrs;
        Console.WriteLine($"    Attributes: aligned to testfire layout ({newAttrs.Count} attrs, was {origCount})");
    }

    /// <summary>
    /// Strip prod-only static options from the shader model.
    /// 
    /// CRITICAL: Must also zero out the removed options' bits in the key table!
    /// Each option occupies specific bits in one of the StaticKeyLength int entries,
    /// defined by Bit32Index (which int), Bit32Mask (which bits), Bit32Shift.
    /// 
    /// If we only remove the option from the dict but leave its bits in the key table,
    /// the game's runtime key comparison (which is exact/bitwise) will fail because:
    /// - WriteDefaultKey skips removed options → those bit positions are 0 in the generated key
    /// - Key table still has non-zero bits → SequenceEqual fails → no program → invisible
    /// </summary>
    private static void AdaptStaticOptions(ShaderModel sm, ShaderModel refSm)
    {
        if (refSm == null)
        {
            Console.WriteLine($"    ⚠ No testfire reference — skipping option adaptation");
            return;
        }

        // Prod-only options that must be PRESERVED because they control
        // paint-related program variant selection. Stripping these would
        // cause the lookup to select non-paint program variants, breaking
        // ground paintability.
        var preserveOptions = new HashSet<string>
        {
            "texcoord_select_teamcolormap",  // Team color paint texture mapping
            "enable_force_paint_face",        // Forced paint face 
            "forced_paint_face",              // Which face to paint
            "enable_overlay_paint_on_emission" // Paint on emissive surfaces
        };

        // Identify prod-only static options and save their bit layout info
        // BEFORE removing them from the dict. Skip paint-related options.
        var prodOnlyOptions = new List<(string Name, int Bit32Index, long Bit32Mask)>();
        var preservedCount = 0;
        for (int i = 0; i < sm.StaticOptions.Count; i++)
        {
            string name = sm.StaticOptions.GetKey(i);
            if (!refSm.StaticOptions.ContainsKey(name))
            {
                if (preserveOptions.Contains(name))
                {
                    preservedCount++;
                    continue; // Keep this option — it's paint-related
                }
                var opt = sm.StaticOptions[i];
                prodOnlyOptions.Add((name, opt.Bit32Index, opt.Bit32Mask));
            }
        }

        if (prodOnlyOptions.Count == 0)
        {
            Console.WriteLine($"    No prod-only options to strip");
            return;
        }

        // Zero out removed options' bits in the key table for EVERY program
        int keysPerProgram = sm.StaticKeyLength + sm.DynamicKeyLength;
        if (sm.KeyTable != null && keysPerProgram > 0)
        {
            int programCount = sm.KeyTable.Length / keysPerProgram;
            int bitsCleared = 0;
            for (int prog = 0; prog < programCount; prog++)
            {
                foreach (var opt in prodOnlyOptions)
                {
                    int idx = prog * keysPerProgram + opt.Bit32Index;
                    if (idx < sm.KeyTable.Length)
                    {
                        int oldVal = sm.KeyTable[idx];
                        sm.KeyTable[idx] = (int)(oldVal & ~opt.Bit32Mask);
                        if (oldVal != sm.KeyTable[idx]) bitsCleared++;
                    }
                }
            }
            Console.WriteLine($"    Zeroed key table bits for {prodOnlyOptions.Count} options across {programCount} programs ({bitsCleared} entries changed)");

            // Check for program collisions — programs that now have identical keys
            // after zeroing removed option bits
            var keyHashes = new Dictionary<string, int>(); // key hash → first program index
            int collisions = 0;
            for (int prog = 0; prog < programCount; prog++)
            {
                var keySlice = new int[keysPerProgram];
                Array.Copy(sm.KeyTable, prog * keysPerProgram, keySlice, 0, keysPerProgram);
                string hash = string.Join(",", keySlice);
                if (keyHashes.ContainsKey(hash))
                {
                    if (collisions < 5)
                        Console.WriteLine($"    ⚠ Collision: program {prog} == program {keyHashes[hash]}");
                    collisions++;
                }
                else
                    keyHashes[hash] = prog;
            }
            if (collisions > 0)
                Console.WriteLine($"    ⚠ Total {collisions} program collisions (was {programCount} unique, now {keyHashes.Count} unique)");
        }

        // Remove prod-only options from the StaticOptions dict
        foreach (var opt in prodOnlyOptions)
            sm.StaticOptions.Remove(opt.Name);

        Console.WriteLine($"    Stripped {prodOnlyOptions.Count} prod-only static options, preserved {preservedCount} paint options (StaticKeyLength={sm.StaticKeyLength})");
    }

    /// <summary>
    /// Selectively swap testfire shader bytecode into prod programs for paint pass rendering.
    /// 
    /// For each prod program matching the target gsys_renderstate, finds the corresponding
    /// testfire program (matching shared option values) and replaces the prod bytecode with
    /// testfire bytecode. This allows ground painting to work (testfire bytecode is compatible
    /// with testfire's paint system) while keeping prod bytecode for all other render passes
    /// (preserving visual accuracy).
    /// 
    /// Pass targetRenderState=-1 to swap ALL programs (diagnostic mode).
    /// </summary>
    public static void SwapPaintPassBytecode(
        ShaderModel prodSm, ShaderModel tfSm, int targetRenderState = 2)
    {
        if (tfSm == null)
        {
            Console.WriteLine($"    ⚠ No testfire shader model — skipping bytecode swap");
            return;
        }

        // Find gsys_renderstate option in prod shader model
        ShaderOption rsOption = null;
        for (int i = 0; i < prodSm.StaticOptions.Count; i++)
        {
            if (prodSm.StaticOptions.GetKey(i) == "gsys_renderstate")
            {
                rsOption = prodSm.StaticOptions[i];
                break;
            }
        }

        // Build a lookup for testfire programs: option key → program index
        // The key is built from option values that are SHARED between prod and testfire
        var sharedOptionNames = new List<string>();
        for (int i = 0; i < tfSm.StaticOptions.Count; i++)
        {
            string name = tfSm.StaticOptions.GetKey(i);
            if (prodSm.StaticOptions.ContainsKey(name))
                sharedOptionNames.Add(name);
        }

        int tfKeysPerProg = tfSm.StaticKeyLength + tfSm.DynamicKeyLength;
        var tfProgramLookup = new Dictionary<string, int>(); // option signature → tf program index
        for (int p = 0; p < tfSm.Programs.Count; p++)
        {
            var sig = BuildOptionSignature(tfSm, p, tfKeysPerProg, sharedOptionNames);
            tfProgramLookup[sig] = p; // last one wins if collision
        }

        int keysPerProg = prodSm.StaticKeyLength + prodSm.DynamicKeyLength;
        int swapped = 0, skipped = 0, noMatch = 0;

        for (int p = 0; p < prodSm.Programs.Count; p++)
        {
            // Check gsys_renderstate for this program
            if (targetRenderState >= 0 && rsOption != null)
            {
                int baseIdx = p * keysPerProg;
                int choiceIdx = rsOption.GetChoiceIndex(prodSm.KeyTable[baseIdx + rsOption.Bit32Index]);
                string rsChoice = choiceIdx >= 0 && choiceIdx < rsOption.Choices.Count
                    ? rsOption.Choices.GetKey(choiceIdx) : "?";
                
                if (rsChoice != targetRenderState.ToString())
                {
                    skipped++;
                    continue; // Not the target render state — keep prod bytecode
                }
            }

            // Build option signature for this prod program (shared options only)
            var prodSig = BuildOptionSignature(prodSm, p, keysPerProg, sharedOptionNames);

            // Find matching testfire program
            if (!tfProgramLookup.ContainsKey(prodSig))
            {
                noMatch++;
                continue; // No testfire match — keep prod bytecode
            }

            int tfProgIdx = tfProgramLookup[prodSig];
            var tfVariation = tfSm.GetVariation(tfProgIdx);
            if (tfVariation == null)
            {
                noMatch++;
                continue;
            }

            // CRITICAL: Force-load the testfire variation's bytecode NOW.
            // ShaderVariation uses lazy loading from a stream reference.
            // When we append it to prodSm, we need to ensure the bytecode is loaded
            // before the testfire bfsha source stream is potentially closed.
            var _ = tfVariation.BinaryProgram;

            // Full program metadata replacement.
            // When using testfire bytecode, we MUST use testfire binding metadata.
            // Otherwise, the bytecode will try to read from wrong binding slots.
            var prodProg = prodSm.Programs[p];
            var tfProg = tfSm.Programs[tfProgIdx];

            prodProg.UsedAttributeFlags = tfProg.UsedAttributeFlags;
            
            // Deep copy binding indices
            prodProg.SamplerIndices = tfProg.SamplerIndices.Select(x => new ShaderIndexHeader { 
                VertexLocation = x.VertexLocation, 
                FragmentLocation = x.FragmentLocation 
            }).ToList();
            
            prodProg.UniformBlockIndices = tfProg.UniformBlockIndices.Select(x => new ShaderIndexHeader { 
                VertexLocation = x.VertexLocation, 
                FragmentLocation = x.FragmentLocation 
            }).ToList();

            prodProg.ImageIndices = tfProg.ImageIndices.Select(x => new ShaderIndexHeader { 
                VertexLocation = x.VertexLocation, 
                FragmentLocation = x.FragmentLocation 
            }).ToList();

            prodProg.StorageBufferIndices = tfProg.StorageBufferIndices.Select(x => new ShaderIndexHeader { 
                VertexLocation = x.VertexLocation, 
                FragmentLocation = x.FragmentLocation 
            }).ToList();

            // Append testfire variation and update index
            int newVarIdx = prodSm.BnshFile.Variations.Count;
            prodSm.BnshFile.Variations.Add(tfVariation);
            prodProg.VariationIndex = newVarIdx;
            
            swapped++;
        }

        string mode = targetRenderState < 0 ? "ALL" : $"gsys_renderstate={targetRenderState}";
        Console.WriteLine($"    Bytecode swap ({mode}): {swapped} swapped, {skipped} skipped (wrong rs), {noMatch} no testfire match");
    }

    /// <summary>
    /// Build a string signature from a program's option values for matching purposes.
    /// Only includes options in the sharedNames list.
    /// </summary>
    private static string BuildOptionSignature(
        ShaderModel sm, int programIndex, int keysPerProg, List<string> sharedNames)
    {
        var parts = new List<string>();
        int baseIdx = programIndex * keysPerProg;

        foreach (var name in sharedNames)
        {
            if (!sm.StaticOptions.ContainsKey(name)) continue;
            var opt = sm.StaticOptions[name];
            int choiceIdx = opt.GetChoiceIndex(sm.KeyTable[baseIdx + opt.Bit32Index]);
            string choice = choiceIdx >= 0 && choiceIdx < opt.Choices.Count
                ? opt.Choices.GetKey(choiceIdx) : $"?{choiceIdx}";
            parts.Add($"{name}={choice}");
        }
        return string.Join("|", parts);
    }
}
