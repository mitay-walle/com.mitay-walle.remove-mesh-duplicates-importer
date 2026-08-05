# FBX Mesh Import Tool

An editor-only Unity package that reduces duplicated mesh assets produced by FBX imports.

## Features

- Reuses meshes with identical vertex, normal, tangent, color, UV, bone, and submesh data.
- Optionally detects meshes with the same geometry rotated in vertex space and reuses one mesh while adjusting the target object's local rotation.
- Lets you exclude individual mesh objects from processing, including hierarchy-aware multi-selection.
- Stores per-model settings in `ModelImporter.userData`.
- Emits optional import diagnostics to the Unity Console.

## Installation

Install the package through Unity Package Manager using the Git URL:

```text
https://github.com/mitay-walle/com.mitay-walle.fbx-mesh-import-tool.git
```

The package is editor-only and has no runtime assembly.

## Usage

Open `Tools > GTR > FBX Mesh Import Tool`, or select an FBX asset and use the button in the Model Importer inspector header. Choose an FBX model, configure the mesh reuse options and object rules, then click **Save Settings And Reimport**.

When vertex-rotated mesh reuse is enabled, the importer temporarily applies comparison-friendly settings and restores the original importer settings after processing.

## Requirements

- Unity 2021.3 or newer.
- The Unity Model Importer module included with the Editor.

## License

Released under the MIT License. See [LICENSE](LICENSE).
