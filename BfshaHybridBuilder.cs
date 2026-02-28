using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShaderLibrary;
using ShaderLibrary.Helpers;

namespace HammerheadConverter
{
    /// <summary>
    /// Builds a hybrid bfsha by cherry-picking shader programs from all testfire bfsha files.
    /// Strategy: for each prod material's option combination, find an exact matching program
    /// in any testfire bfsha and copy it into the target bfsha.
    /// </summary>
    public class BfshaHybridBuilder
    {
        /// <summary>
        /// Result of matching a material to a testfire shader program.
        /// </summary>
        public class MatchResult
        {
            public string MaterialName;
            public string ShaderModelName;
            public int ProgramIndex;
            public bool IsExactMatch;
            public int DifferingOptions;
            public Dictionary<string, string> AdaptedOptions;
            public List<string> ChangedOptions;
        }

        /// <summary>
        /// Result of scanning the shadict directory.
        /// </summary>
        public class ScanResult
        {
            /// <summary>Shader model name → list of (source, ShaderModel) from bfsha files.</summary>
            public Dictionary<string, List<(string Source, ShaderModel Sm)>> ShaderModels;

            /// <summary>Material name → (shader model name, known-good shader options) from testfire bfres files.</summary>
            public Dictionary<string, (string ShaderModel, Dictionary<string, string> Options)> KnownGoodMaterials;
        }

        /// <summary>
        /// Scans all SZS files in the shadict directory, extracting both:
        /// 1. bfsha shader models (for program matching)
        /// 2. bfres material shader options (known-good option sets)
        /// Single pass — each SZS is decompressed only once.
        /// </summary>
        public static ScanResult ScanShadictAll(string shadictDir)
        {
            var shaderModels = new Dictionary<string, List<(string Source, ShaderModel Sm)>>();
            var knownGoodMats = new Dictionary<string, (string ShaderModel, Dictionary<string, string> Options)>();
            int totalSzs = 0, totalBfsha = 0, totalBfres = 0, totalMats = 0;

            foreach (var szsFile in Directory.GetFiles(shadictDir, "*.szs").OrderBy(f => f))
            {
                totalSzs++;
                string szsName = Path.GetFileNameWithoutExtension(szsFile);

                try
                {
                    byte[] decompressed = Oead.Yaz0DecompressFile(szsFile);
                    var sarcFiles = Oead.SarcRead(decompressed);

                    // Extract bfsha files
                    foreach (var bf in sarcFiles.Where(f => Path.GetExtension(f.Key).Equals(".bfsha", StringComparison.OrdinalIgnoreCase)))
                    {
                        totalBfsha++;
                        var bfsha = new BfshaFile(new MemoryStream(bf.Value));

                        for (int i = 0; i < bfsha.ShaderModels.Count; i++)
                        {
                            var sm = bfsha.ShaderModels.Values.ElementAt(i);
                            string modelName = sm.Name;

                            if (!shaderModels.ContainsKey(modelName))
                                shaderModels[modelName] = new List<(string Source, ShaderModel Sm)>();
                            shaderModels[modelName].Add(($"{szsName}/{bf.Key}", sm));
                        }
                    }

                    // Extract bfres files for known-good material options
                    foreach (var bf in sarcFiles.Where(f => Path.GetExtension(f.Key).Equals(".bfres", StringComparison.OrdinalIgnoreCase)))
                    {
                        totalBfres++;
                        try
                        {
                            var bfres = new BfresLibrary.ResFile(new MemoryStream(bf.Value));

                            foreach (var model in bfres.Models.Values)
                            {
                                foreach (var mat in model.Materials.Values)
                                {
                                    if (mat.ShaderAssign?.ShaderOptions == null) continue;
                                    // Only store if we haven't seen this material yet
                                    // (first occurrence wins — could also merge/prefer field maps)
                                    if (knownGoodMats.ContainsKey(mat.Name)) continue;

                                    var opts = new Dictionary<string, string>();
                                    foreach (var opt in mat.ShaderAssign.ShaderOptions)
                                        opts[opt.Key] = opt.Value;
                                    string smName = mat.ShaderAssign.ShadingModelName ?? "";
                                    knownGoodMats[mat.Name] = (smName, opts);
                                    totalMats++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"  Warning: Failed to parse bfres in {szsName}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Warning: Failed to process {szsName}: {ex.Message}");
                }
            }

            Console.WriteLine($"Scanned {totalSzs} SZS files: {totalBfsha} bfsha, {totalBfres} bfres, {totalMats} unique materials");
            foreach (var kvp in shaderModels)
                Console.WriteLine($"  {kvp.Key}: {kvp.Value.Count} sources, max {kvp.Value.Max(v => v.Sm.Programs.Count)} programs");

            return new ScanResult
            {
                ShaderModels = shaderModels,
                KnownGoodMaterials = knownGoodMats
            };
        }

        /// <summary>
        /// Legacy method — scans only bfsha. Use ScanShadictAll for both bfsha+bfres.
        /// </summary>
        public static Dictionary<string, List<(string Source, ShaderModel Sm)>> ScanShadict(string shadictDir)
        {
            return ScanShadictAll(shadictDir).ShaderModels;
        }

        /// <summary>
        /// Result of assembling a hybrid bfsha.
        /// </summary>
        public class AssemblyResult
        {
            /// <summary>Materials that got exact programs (pre-existing or cherry-picked).</summary>
            public List<string> CoveredMaterials = new List<string>();
            /// <summary>Materials that need fallback (no exact match in any testfire bfsha).</summary>
            public List<string> FallbackMaterials = new List<string>();
            /// <summary>Number of programs cherry-picked from other bfsha files.</summary>
            public int CherryPickedCount;
            /// <summary>Number of programs that already existed in the target bfsha.</summary>
            public int PreExistingCount;
        }

        /// <summary>
        /// Cherry-pick exact matching shader programs from all testfire bfsha into the target bfsha.
        /// For each prod material option combination not already in the target:
        ///   1. Search all testfire bfsha for an exact match
        ///   2. If found: copy the ShaderVariation and BfshaShaderProgram into the target
        ///   3. If not found: mark material for fallback adaptation
        /// </summary>
        public static AssemblyResult AssembleHybridBfsha(
            BfshaFile targetBfsha,
            Dictionary<string, List<(string Source, ShaderModel Sm)>> allModels,
            List<(string MatName, string ShaderModelName, Dictionary<string, string> Options)> materialOptionSets)
        {
            var result = new AssemblyResult();

            // Group materials by shader model name
            var byModel = new Dictionary<string, List<(string MatName, Dictionary<string, string> Options)>>();
            foreach (var mat in materialOptionSets)
            {
                if (!byModel.ContainsKey(mat.ShaderModelName))
                    byModel[mat.ShaderModelName] = new List<(string, Dictionary<string, string>)>();
                byModel[mat.ShaderModelName].Add((mat.MatName, mat.Options));
            }

            foreach (var modelGroup in byModel)
            {
                string modelName = modelGroup.Key;

                // Find matching shader model in target bfsha
                ShaderModel targetSm = null;
                for (int i = 0; i < targetBfsha.ShaderModels.Count; i++)
                {
                    var sm = targetBfsha.ShaderModels.Values.ElementAt(i);
                    if (sm.Name == modelName) { targetSm = sm; break; }
                }

                if (targetSm == null)
                {
                    Console.WriteLine($"  ⚠ Shader model '{modelName}' not in target bfsha — all materials need fallback");
                    foreach (var mat in modelGroup.Value)
                        result.FallbackMaterials.Add(mat.MatName);
                    continue;
                }

                // Deduplicate: track which option combos we've already processed
                var processedKeys = new HashSet<string>();

                foreach (var mat in modelGroup.Value)
                {
                    var sanitized = SanitizeOptions(mat.Options, targetSm);

                    // Build a dedup key from sorted sanitized options
                    string dedupKey = string.Join("|",
                        sanitized.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));

                    if (processedKeys.Contains(dedupKey))
                    {
                        // Already added this exact combo
                        result.CoveredMaterials.Add(mat.MatName);
                        continue;
                    }
                    processedKeys.Add(dedupKey);

                    // Check if target already has this program
                    try
                    {
                        int existingIdx = targetSm.GetProgramIndex(sanitized);
                        if (existingIdx >= 0)
                        {
                            result.CoveredMaterials.Add(mat.MatName);
                            result.PreExistingCount++;
                            Console.WriteLine($"  ✓ '{mat.MatName}': already in target (program {existingIdx})");
                            continue;
                        }
                    }
                    catch { /* Invalid options for target — try other sources */ }

                    // Search all testfire bfsha for an exact match
                    bool found = false;
                    if (allModels.ContainsKey(modelName))
                    {
                        foreach (var source in allModels[modelName])
                        {
                            if (source.Sm == targetSm) continue; // Skip target itself

                            var sourceSanitized = SanitizeOptions(mat.Options, source.Sm);
                            try
                            {
                                int srcIdx = source.Sm.GetProgramIndex(sourceSanitized);
                                if (srcIdx >= 0)
                                {
                                    // Found! Cherry-pick this program
                                    CherryPickProgram(targetSm, source.Sm, srcIdx, sanitized);
                                    result.CoveredMaterials.Add(mat.MatName);
                                    result.CherryPickedCount++;
                                    Console.WriteLine($"  + '{mat.MatName}': cherry-picked from {source.Source} (program {srcIdx})");
                                    found = true;
                                    break;
                                }
                            }
                            catch { /* Option mismatch for this source — try next */ }
                        }
                    }

                    if (!found)
                    {
                        result.FallbackMaterials.Add(mat.MatName);
                        Console.WriteLine($"  ✗ '{mat.MatName}': no exact match in any testfire bfsha");
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Copy a program from a source ShaderModel into the target ShaderModel.
        /// Copies the ShaderVariation (GPU code) and creates a new BfshaShaderProgram
        /// with the source's resource binding locations.
        /// </summary>
        private static void CherryPickProgram(
            ShaderModel targetSm,
            ShaderModel sourceSm,
            int sourceProgramIndex,
            Dictionary<string, string> targetOptions)
        {
            var sourceProgram = sourceSm.Programs[sourceProgramIndex];

            // 1. Copy the ShaderVariation (compiled GPU bytecode) into target's BnshFile
            var sourceVariation = sourceSm.BnshFile.Variations[sourceProgram.VariationIndex];
            // Force lazy-load the binary program data before we reference it
            var _ = sourceVariation.BinaryProgram;

            targetSm.BnshFile.Variations.Add(sourceVariation);
            int newVariationIndex = targetSm.BnshFile.Variations.Count - 1;

            // 2. Create new BfshaShaderProgram with copied binding locations
            var newProgram = new BfshaShaderProgram();
            newProgram.VariationIndex = newVariationIndex;
            newProgram.UsedAttributeFlags = sourceProgram.UsedAttributeFlags;
            newProgram.Flags = sourceProgram.Flags;

            // Copy sampler indices (binding locations)
            foreach (var idx in sourceProgram.SamplerIndices)
                newProgram.SamplerIndices.Add(new ShaderIndexHeader
                {
                    VertexLocation = idx.VertexLocation,
                    FragmentLocation = idx.FragmentLocation,
                    GeoemetryLocation = idx.GeoemetryLocation,
                    ComputeLocation = idx.ComputeLocation
                });

            // Copy uniform block indices
            foreach (var idx in sourceProgram.UniformBlockIndices)
                newProgram.UniformBlockIndices.Add(new ShaderIndexHeader
                {
                    VertexLocation = idx.VertexLocation,
                    FragmentLocation = idx.FragmentLocation,
                    GeoemetryLocation = idx.GeoemetryLocation,
                    ComputeLocation = idx.ComputeLocation
                });

            // Copy storage buffer indices
            foreach (var idx in sourceProgram.StorageBufferIndices)
                newProgram.StorageBufferIndices.Add(new ShaderIndexHeader
                {
                    VertexLocation = idx.VertexLocation,
                    FragmentLocation = idx.FragmentLocation,
                    GeoemetryLocation = idx.GeoemetryLocation,
                    ComputeLocation = idx.ComputeLocation
                });

            // Copy image indices
            foreach (var idx in sourceProgram.ImageIndices)
                newProgram.ImageIndices.Add(new ShaderIndexHeader
                {
                    VertexLocation = idx.VertexLocation,
                    FragmentLocation = idx.FragmentLocation,
                    GeoemetryLocation = idx.GeoemetryLocation,
                    ComputeLocation = idx.ComputeLocation
                });

            // 3. Expand key table with empty keys for new program
            int keysPerProgram = targetSm.StaticKeyLength + targetSm.DynamicKeyLength;
            int[] newKeys = new int[keysPerProgram];
            targetSm.KeyTable = targetSm.KeyTable.Concat(newKeys).ToArray();

            // 4. Add program and set option keys
            int newProgramIndex = targetSm.Programs.Count;
            targetSm.Programs.Add(newProgram);
            targetSm.SetProgramOptions(newProgramIndex, targetOptions);
        }

        /// <summary>
        /// For a given material's options, find the best matching program across all
        /// testfire bfsha files for the specified shader model.
        /// </summary>
        public static MatchResult FindBestMatch(
            string materialName,
            string shaderModelName,
            Dictionary<string, string> materialOptions,
            List<(string Source, ShaderModel Sm)> testfireModels)
        {
            // First try exact match across all sources
            // Sanitize options per-model since different bfsha may have different choice sets
            foreach (var entry in testfireModels)
            {
                var sanitized = SanitizeOptions(materialOptions, entry.Sm);
                try
                {
                    int idx = entry.Sm.GetProgramIndex(sanitized);
                    if (idx >= 0)
                    {
                        return new MatchResult
                        {
                            MaterialName = materialName,
                            ShaderModelName = shaderModelName,
                            ProgramIndex = idx,
                            IsExactMatch = true,
                            DifferingOptions = 0,
                            AdaptedOptions = sanitized,
                            ChangedOptions = new List<string>()
                        };
                    }
                }
                catch
                {
                    // Choice value mismatch — skip this model, try others
                }
            }

            // No exact match — find closest program using key distance
            // Use the shader model with the most programs as the search space
            var bestSm = testfireModels.OrderByDescending(t => t.Sm.Programs.Count).First().Sm;
            return FindClosestProgram(materialName, shaderModelName, materialOptions, bestSm);
        }

        /// <summary>
        /// Sanitize material options for a specific shader model:
        /// - Remove options whose names don't exist in the shader model
        /// - Remove options whose values aren't valid choices in the shader model
        /// </summary>
        private static Dictionary<string, string> SanitizeOptions(
            Dictionary<string, string> options, ShaderModel sm)
        {
            var result = new Dictionary<string, string>();

            foreach (var kvp in options)
            {
                // Check static options
                bool found = false;
                for (int i = 0; i < sm.StaticOptions.Count; i++)
                {
                    var opt = sm.StaticOptions[i];
                    if (opt.Name == kvp.Key)
                    {
                        // Verify the choice value is valid
                        if (opt.Choices.GetIndex(kvp.Value) >= 0)
                            result[kvp.Key] = kvp.Value;
                        found = true;
                        break;
                    }
                }
                if (found) continue;

                // Check dynamic options
                for (int i = 0; i < sm.DynamicOptions.Count; i++)
                {
                    var opt = sm.DynamicOptions[i];
                    if (opt.Name == kvp.Key)
                    {
                        if (opt.Choices.GetIndex(kvp.Value) >= 0)
                            result[kvp.Key] = kvp.Value;
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Options that are critical for visibility — changing these causes materials to disappear.
        /// The fallback algorithm heavily penalizes programs that differ on these options.
        /// </summary>
        private static readonly HashSet<string> CriticalOptions = new HashSet<string>
        {
            "gsys_renderstate",
            "gsys_alpha_test_enable",
            "gsys_alpha_test_func",
            "gsys_enable_color_buffer",
            "blitz_xlu_type",
            "enable_albedo_tex",
            "blitz_refract_type",
            "enable_calc_color0",
            "enable_calc_color1",
            "blitz_paint_type",
            "blitz_obj_paint_type",
            "blitz_map_paint",
            "gsys_stencil",
            "gsys_priority",
            "enable_normal_map",
            "enable_emission",
        };

        /// <summary>
        /// Find the closest matching program by computing weighted key distance.
        /// Critical rendering options get heavy penalties to prevent changing them.
        /// </summary>
        public static MatchResult FindClosestProgram(
            string materialName,
            string shaderModelName,
            Dictionary<string, string> materialOptions,
            ShaderModel sm)
        {
            // Sanitize options first to remove invalid choice values
            var cleanOptions = SanitizeOptions(materialOptions, sm);

            // Generate the material's key from sanitized options
            int[] materialKey;
            try
            {
                materialKey = ShaderOptionSearcher.WriteOptionKeys(sm, cleanOptions);
            }
            catch
            {
                materialKey = new int[sm.StaticKeyLength + sm.DynamicKeyLength];
            }

            // Build a map of Bit32Index → weight for critical options
            var criticalSlots = new Dictionary<int, int>(); // slot index → penalty weight
            for (int j = 0; j < sm.StaticOptions.Count; j++)
            {
                var opt = sm.StaticOptions[j];
                if (CriticalOptions.Contains(opt.Name))
                    criticalSlots[opt.Bit32Index] = 1000;
            }

            int numKeysPerProgram = sm.StaticKeyLength + sm.DynamicKeyLength;
            int bestProgram = sm.DefaultProgramIndex >= 0 ? sm.DefaultProgramIndex : 0;
            int bestDistance = int.MaxValue;

            for (int i = 0; i < sm.Programs.Count; i++)
            {
                int distance = 0;
                int baseIdx = numKeysPerProgram * i;

                for (int k = 0; k < numKeysPerProgram; k++)
                {
                    int diff = materialKey[k] ^ sm.KeyTable[baseIdx + k];
                    if (diff == 0) continue;

                    // Apply weight: critical option slots get 1000x penalty per differing bit
                    int weight = criticalSlots.ContainsKey(k) ? 1000 : 1;
                    while (diff != 0)
                    {
                        distance += weight;
                        diff &= diff - 1;
                    }
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestProgram = i;
                    if (distance == 0) break;
                }
            }

            // Reconstruct the adapted options from the selected program's key
            var adapted = ReconstructOptionsFromProgram(sm, bestProgram, materialOptions);

            return new MatchResult
            {
                MaterialName = materialName,
                ShaderModelName = shaderModelName,
                ProgramIndex = bestProgram,
                IsExactMatch = false,
                DifferingOptions = bestDistance,
                AdaptedOptions = adapted.options,
                ChangedOptions = adapted.changed
            };
        }

        /// <summary>
        /// Given a program index, reconstruct the shader options that would select it.
        /// Preserves original material options where possible, only changing what's needed.
        /// </summary>
        private static (Dictionary<string, string> options, List<string> changed) ReconstructOptionsFromProgram(
            ShaderModel sm, int programIndex, Dictionary<string, string> originalOptions)
        {
            var result = new Dictionary<string, string>(originalOptions);
            var changed = new List<string>();

            int numKeysPerProgram = sm.StaticKeyLength + sm.DynamicKeyLength;
            int baseIndex = numKeysPerProgram * programIndex;

            // Update static options to match the program's key
            for (int j = 0; j < sm.StaticOptions.Count; j++)
            {
                var option = sm.StaticOptions[j];
                int choiceIndex = option.GetChoiceIndex(sm.KeyTable[baseIndex + option.Bit32Index]);
                if (choiceIndex < 0 || choiceIndex >= option.Choices.Count)
                    continue;

                string programChoice = option.Choices.GetKey(choiceIndex);
                string materialChoice = originalOptions.ContainsKey(option.Name) ?
                    originalOptions[option.Name] : option.DefaultChoice;

                if (programChoice != materialChoice)
                {
                    result[option.Name] = programChoice;
                    changed.Add($"{option.Name}: {materialChoice} → {programChoice}");
                }
            }

            return (result, changed);
        }

        /// <summary>
        /// Public wrapper: reconstruct options for a specific program index.
        /// Returns the options dictionary, or null on failure.
        /// </summary>
        public static Dictionary<string, string> ReconstructOptionsForProgram(
            ShaderModel sm, int programIndex, Dictionary<string, string> originalOptions)
        {
            try
            {
                var (options, _) = ReconstructOptionsFromProgram(sm, programIndex, originalOptions);
                return options;
            }
            catch { return null; }
        }

        /// <summary>
        /// Strip shader options that don't exist in the testfire bfsha shader model.
        /// These are prod-only options that the testfire shader programs don't use.
        /// </summary>
        public static Dictionary<string, string> StripProdOnlyOptions(
            Dictionary<string, string> materialOptions, ShaderModel tfShaderModel)
        {
            var validOptionNames = new HashSet<string>();
            for (int i = 0; i < tfShaderModel.StaticOptions.Count; i++)
                validOptionNames.Add(tfShaderModel.StaticOptions.GetKey(i));
            for (int i = 0; i < tfShaderModel.DynamicOptions.Count; i++)
                validOptionNames.Add(tfShaderModel.DynamicOptions.GetKey(i));

            var result = new Dictionary<string, string>();
            foreach (var kvp in materialOptions)
            {
                if (validOptionNames.Contains(kvp.Key))
                    result[kvp.Key] = kvp.Value;
            }
            return result;
        }
    }
}
