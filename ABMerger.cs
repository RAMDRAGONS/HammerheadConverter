using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BfresLibrary;
using KclLibrary;

namespace HammerheadConverter;

/// <summary>
/// Merges A/B model variants in BFRES and KCL files.
///
/// Splatoon 2 Obj SZS archives can contain mirrored A/B model variants for
/// team differentiation. Testfire sometimes wants these as a singular model
/// so we want to cleanly merge them. The pattern is:
///   BFRES models: {Base}A, {Base}A_Map, {Base}B, {Base}B_Map
///   KCL files:    {Base}A.kcl, {Base}B.kcl
///
/// After merging: {Base}, {Base}_Map models and {Base}.kcl — A/B originals removed.
/// </summary>
public static class ABMerger
{
    /// <summary>
    /// Merges A/B model pairs in a BFRES. For each pair, model A is used as the base
    /// and model B's shapes, materials, vertex buffers, and skeleton bones are appended.
    /// Shape MaterialIndex and VertexBufferIndex values from B are offset accordingly.
    /// The merged model is renamed to strip the trailing "A".
    /// Returns a list of merged base names for logging.
    /// </summary>
    public static List<string> MergeModelsAB(ResFile bfres)
    {
        var merged = new List<string>();

        // Collect model names ending in A that have a matching B counterpart
        var modelNames = bfres.Models.Keys.ToList();
        var abPairs = new List<(string aName, string bName, string mergedName)>();

        foreach (var name in modelNames)
        {
            // Match names ending in A (but not A_Map — those are handled separately)
            if (!name.EndsWith("A") || name.EndsWith("A_Map"))
                continue;

            string baseName = name.Substring(0, name.Length - 1);
            string bName = baseName + "B";

            if (modelNames.Contains(bName))
                abPairs.Add((name, bName, baseName));
        }

        // Also handle A_Map / B_Map pairs
        foreach (var name in modelNames)
        {
            if (!name.EndsWith("A_Map"))
                continue;

            string baseName = name.Substring(0, name.Length - 5); // strip "A_Map"
            string bName = baseName + "B_Map";
            string mergedName = baseName + "_Map";

            if (modelNames.Contains(bName))
                abPairs.Add((name, bName, mergedName));
        }

        if (abPairs.Count == 0)
            return merged;

        // Build new model dict preserving insertion order
        var newModels = new ResDict<Model>();

        // Track which model names are consumed by merging
        var consumed = new HashSet<string>();
        foreach (var pair in abPairs)
        {
            consumed.Add(pair.aName);
            consumed.Add(pair.bName);
        }

        // Process models in original order, merging where applicable
        foreach (var entry in bfres.Models)
        {
            if (consumed.Contains(entry.Key))
            {
                // Check if this is an "A" model (not the B counterpart)
                var pair = abPairs.FirstOrDefault(p => p.aName == entry.Key);
                if (pair.aName != null)
                {
                    var modelA = bfres.Models[pair.aName];
                    var modelB = bfres.Models[pair.bName];

                    MergeModelPair(modelA, modelB);
                    modelA.Name = pair.mergedName;

                    newModels.Add(pair.mergedName, modelA);
                    merged.Add(pair.mergedName);

                    Console.WriteLine($"    AB-Merge: {pair.aName} + {pair.bName} → {pair.mergedName} " +
                        $"({modelA.Shapes.Count} shapes, {modelA.Materials.Count} materials)");
                }
                // B models are consumed silently
            }
            else
            {
                // Non-AB model — pass through
                newModels.Add(entry.Key, entry.Value);
            }
        }

        // Replace the model dictionary
        bfres.Models.Clear();
        foreach (var entry in newModels)
            bfres.Models.Add(entry.Key, entry.Value);

        return merged;
    }

    /// <summary>
    /// Merges model B's data into model A. For materials, builds an index remap:
    /// if B has a material with the same name as one in A, B's shapes are remapped
    /// to use A's existing material index; otherwise the material is appended.
    /// Vertex buffers are always appended. Shapes are appended with remapped indices.
    /// Skeleton bones from B not in A are appended.
    /// </summary>
    private static void MergeModelPair(Model modelA, Model modelB)
    {
        int vbOffset = modelA.VertexBuffers.Count;

        // Build material name → index lookup for A
        var matNameToIndexA = new Dictionary<string, int>();
        int idx = 0;
        foreach (var mat in modelA.Materials)
            matNameToIndexA[mat.Key] = idx++;

        // Build material index remap for B: B's original index → merged index in A
        var matRemap = new Dictionary<int, int>();
        int bIdx = 0;
        foreach (var mat in modelB.Materials)
        {
            if (matNameToIndexA.TryGetValue(mat.Key, out int existingIdx))
            {
                // Material already exists in A — remap to A's index
                matRemap[bIdx] = existingIdx;
            }
            else
            {
                // New material — append to A and record its new index
                int newIdx = modelA.Materials.Count;
                modelA.Materials.Add(mat.Key, mat.Value);
                matRemap[bIdx] = newIdx;
            }
            bIdx++;
        }

        // Append B's vertex buffers
        foreach (var vb in modelB.VertexBuffers)
            modelA.VertexBuffers.Add(vb);

        // Append B's shapes with remapped material and vertex buffer indices
        var existingShapeNames = new HashSet<string>(modelA.Shapes.Keys);
        foreach (var shape in modelB.Shapes)
        {
            shape.Value.MaterialIndex = (ushort)matRemap[shape.Value.MaterialIndex];
            shape.Value.VertexBufferIndex = (ushort)(shape.Value.VertexBufferIndex + vbOffset);

            // Handle duplicate shape names by suffixing with _B
            string shapeName = shape.Key;
            if (existingShapeNames.Contains(shapeName))
                shapeName = shapeName + "_B";

            modelA.Shapes.Add(shapeName, shape.Value);
            existingShapeNames.Add(shapeName);
        }

        // Append B's skeleton bones that don't already exist in A
        var existingBones = new HashSet<string>(modelA.Skeleton.Bones.Keys);
        foreach (var bone in modelB.Skeleton.Bones)
        {
            if (!existingBones.Contains(bone.Key))
                modelA.Skeleton.Bones.Add(bone.Key, bone.Value);
        }
    }

    /// <summary>
    /// Merges A/B KCL file pairs in a SARC file dictionary.
    /// Loads both KCLs, extracts all triangles, rebuilds a merged KCL, and replaces
    /// the A/B entries with a single merged entry.
    /// Returns a list of merged KCL base names for logging.
    /// </summary>
    public static List<string> MergeKclAB(Dictionary<string, byte[]> sarcFiles)
    {
        var merged = new List<string>();

        // Find A/B KCL pairs
        var kclKeys = sarcFiles.Keys.Where(k => k.EndsWith(".kcl", StringComparison.OrdinalIgnoreCase)).ToList();
        var pairs = new List<(string aKey, string bKey, string mergedKey)>();

        foreach (var key in kclKeys)
        {
            string nameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(key);
            if (!nameWithoutExt.EndsWith("A"))
                continue;

            string baseName = nameWithoutExt.Substring(0, nameWithoutExt.Length - 1);
            string bKey = baseName + "B.kcl";

            // Handle path prefixes if present
            string dir = System.IO.Path.GetDirectoryName(key) ?? "";
            string fullBKey = string.IsNullOrEmpty(dir) ? bKey : System.IO.Path.Combine(dir, bKey);
            string mergedKey = string.IsNullOrEmpty(dir) ? baseName + ".kcl" : System.IO.Path.Combine(dir, baseName + ".kcl");

            if (sarcFiles.ContainsKey(fullBKey))
                pairs.Add((key, fullBKey, mergedKey));
        }

        foreach (var pair in pairs)
        {
            try
            {
                // Load both KCL files
                var kclA = new KCLFile(new MemoryStream(sarcFiles[pair.aKey]));
                var kclB = new KCLFile(new MemoryStream(sarcFiles[pair.bKey]));

                // Extract all triangles from both
                var allTriangles = new List<Triangle>();
                ExtractTriangles(kclA, allTriangles);
                ExtractTriangles(kclB, allTriangles);

                // Build merged KCL (little-endian for Switch, V2)
                var mergedKcl = new KCLFile(allTriangles, FileVersion.Version2, isBigEndian: false);

                // Serialize
                using var ms = new MemoryStream();
                mergedKcl.Save(ms);

                // Replace in SARC
                sarcFiles.Remove(pair.aKey);
                sarcFiles.Remove(pair.bKey);
                sarcFiles[pair.mergedKey] = ms.ToArray();

                merged.Add(pair.mergedKey);
                Console.WriteLine($"    AB-Merge KCL: {pair.aKey} + {pair.bKey} → {pair.mergedKey} " +
                    $"({allTriangles.Count} triangles, {mergedKcl.Models.Count} models)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    WARN: Failed to merge KCL {pair.aKey} + {pair.bKey}: {ex.Message}");
            }
        }

        return merged;
    }

    /// <summary>
    /// Extracts all triangles from a KCL file into the given list.
    /// Preserves collision attribute flags.
    /// </summary>
    private static void ExtractTriangles(KCLFile kcl, List<Triangle> outTriangles)
    {
        foreach (var model in kcl.Models)
        {
            foreach (var prism in model.Prisms)
            {
                var tri = model.GetTriangle(prism);
                if (!tri.HasNan())
                {
                    tri.Attribute = prism.CollisionFlags;
                    outTriangles.Add(tri);
                }
            }
        }
    }
}
