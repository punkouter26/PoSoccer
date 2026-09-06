using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace PoSoccer.EditorTools
{
    /// <summary>
    /// Builds the project's sprite atlases.
    ///
    /// WHY THIS IS A TOOL AND NOT A CHECKED-IN YAML FILE. A .spriteatlasv2 is
    /// Unity-serialized and version-sensitive, and this project is already on its
    /// second Unity minor this release cycle. Hand-authoring the YAML - which is
    /// how every other non-MCP-creatable asset here has had to be made - would
    /// produce a file that silently stops packing after an upgrade, and a sprite
    /// atlas that stops packing looks exactly like one that is working. Going
    /// through SpriteAtlasAsset means Unity writes the format it currently reads.
    ///
    /// WHY IT MATTERS. .claude/rules/performance.md makes atlasing MANDATORY for
    /// 2D and asks for the lowest draw-call count possible, and until 2026-09-05
    /// the project contained no atlas at all: `find Assets -name "*.spriteatlas*"`
    /// returned nothing. Every sprite was its own texture and therefore its own
    /// batch break.
    ///
    /// Two atlases, split by rendering layer as that rule requires:
    ///  - PitchAtlas: everything drawn on the field every frame.
    ///  - The runtime shapes (replay ghosts, crowd tiles, rings, shadows) are NOT
    ///    here - they are generated at runtime and share Agent_Art's own single
    ///    page, which is the same optimisation by a different mechanism because
    ///    they have no source PNG to pack.
    ///
    /// Assets/Sprites/Icons is deliberately EXCLUDED. Those are Android launcher
    /// icons consumed by the manifest, never rendered by the game; packing them
    /// would put megabytes of art nothing draws into a runtime atlas.
    /// </summary>
    public static class Editor_BuildSpriteAtlases
    {
        const string ATLAS_DIR = "Assets/Art/Atlases";

        static readonly string[] PitchSprites =
        {
            "Assets/Sprites/pitch.png",
            "Assets/Sprites/backdrop.png",
            "Assets/Sprites/ball.png",
            "Assets/Sprites/tile.png",
        };

        [MenuItem("PoSoccer/Build Sprite Atlases")]
        public static void Build()
        {
            if (EditorSettings.spritePackerMode == SpritePackerMode.Disabled)
            {
                Debug.LogError(
                    "Editor_BuildSpriteAtlases: the Sprite Packer is DISABLED " +
                    "(Project Settings > Editor > Sprite Packer). An atlas will be " +
                    "created but nothing will ever pack into it. Aborting rather " +
                    "than leaving an atlas that looks like it is working.");
                return;
            }

            Directory.CreateDirectory(ATLAS_DIR);
            BuildAtlas("PitchAtlas", PitchSprites);
            AssetDatabase.Refresh();
        }

        static void BuildAtlas(string name, string[] spritePaths)
        {
            var members = new List<Object>();
            for (int i = 0; i < spritePaths.Length; i++)
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Texture2D>(spritePaths[i]);
                if (sprite == null)
                {
                    // Named-but-missing is worth saying out loud. This project
                    // routes every PNG through Git LFS, and a clone made without
                    // `git lfs install` leaves them as 130-byte pointer stubs that
                    // fail to import - which would otherwise show up here as an
                    // atlas that is quietly half empty.
                    Debug.LogWarning($"Editor_BuildSpriteAtlases: {spritePaths[i]} " +
                                     "did not load; skipping. If several are missing, " +
                                     "run `git lfs install --local; git lfs pull`.");
                    continue;
                }
                members.Add(sprite);
            }

            if (members.Count == 0)
            {
                Debug.LogError($"Editor_BuildSpriteAtlases: {name} has no members; not written.");
                return;
            }

            // MEMBERSHIP goes on the ASSET; SETTINGS go on the IMPORTER.
            //
            // SpriteAtlasAsset.SetPackingSettings / SetTextureSettings /
            // SetPlatformSettings all exist and all are [Obsolete] in Unity 6 -
            // they compile, warn, and are documented for removal. CLAUDE.md
            // records that this Unity line turns deprecations into hard errors,
            // so using them would have left a build break waiting on the next
            // upgrade. The supported path is to save the asset, import it, then
            // configure the SpriteAtlasImporter the import produced.
            var asset = new SpriteAtlasAsset();
            asset.Add(members.ToArray());

            string path = $"{ATLAS_DIR}/{name}.spriteatlasv2";
            SpriteAtlasAsset.Save(asset, path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            if (AssetImporter.GetAtPath(path) is SpriteAtlasImporter importer)
            {
                importer.packingSettings = new SpriteAtlasPackingSettings
                {
                    enableRotation = true,
                    enableTightPacking = true,
                    // padding 2 rather than the default 4: these sprites are small
                    // and the pitch is a single large quad, so the wasted border
                    // adds up faster than the bleed risk does.
                    padding = 2,
                };

                importer.textureSettings = new SpriteAtlasTextureSettings
                {
                    // Point filtering aliases badly when the replay camera pushes
                    // in; the sprites are authored above the size they draw at.
                    filterMode = FilterMode.Bilinear,
                    generateMipMaps = false,
                    sRGB = true,
                };

                importer.SetPlatformSettings(new TextureImporterPlatformSettings
                {
                    name = "DefaultTexturePlatform",
                    // 2048 is the mobile cap performance.md sets. The whole pitch
                    // set fits inside it with room to spare.
                    maxTextureSize = 2048,
                    format = TextureImporterFormat.Automatic,
                    textureCompression = TextureImporterCompression.Compressed,
                    overridden = true,
                });

                importer.SaveAndReimport();
            }

            Debug.Log($"Editor_BuildSpriteAtlases: {name} written to {path} " +
                      $"with {members.Count} sprite(s). Verify the packed result in " +
                      "the atlas inspector's Pack Preview, and confirm the win on the " +
                      "F3 telemetry overlay's Draw calls / Batches rows.");
        }
    }
}
