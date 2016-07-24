# What is this sorcery?

A collection of *DirectX 12 C# samples* from Frank D. Luna's book [Introduction to 3D Game Programming with Direct3D 12.0](http://d3dcoder.net/d3d12.htm). Samples have been ported to the .NET environment using [SharpDX](http://sharpdx.org/).

# Building

All the samples will compile with Visual Studio 2015+ and run on Windows 10 with DirectX 12 capable graphics hardware.

# Samples

## [InitDirect3D](Samples/InitDirect3D)
<img src="./Images/InitDirect3D.jpg" height="96px" align="right">

Sets up a basic Direct3D 12 enabled window. Introduces initializing Direct3D, setting up a game loop, building a base framework upon which next samples are built.

## [Box](Samples/Box)
<img src="./Images/Box.jpg" height="96px" align="right">

Renders a colored box. Introduces vertices and input layouts, vertex and index buffers, vertex and pixel shaders, constant buffers, compiling shaders, rasterizer state, pipeline state object. 

## [Shapes](Samples/Shapes)
<img src="./Images/Shapes.jpg" height="96px" align="right">

Renders multiple objects in a scene. Introduces using multiple world transformation matrices, drawing multiple objects from a single vertex and index buffer.

## [LandAndWaves](Samples/LandAndWaves)
<img src="./Images/LandAndWaves.jpg" height="96px" align="right">

Constructs a basic terrain and animated water geometry. Introduces dynamic vertex buffers.

## [LitWaves](Samples/LitWaves)
<img src="./Images/LitWaves.jpg" height="96px" align="right">

Adds lighting to previous land and waves sample. Introduces diffuse, ambient and specular lighting, materials, directional light. 

## [LitColumns](Samples/LitColumns)
<img src="./Images/LitColumns.jpg" height="96px" align="right">

Introduces parsing and loading a skeleton model mesh from a custom model format.

## [Crate](Samples/Crate)
<img src="./Images/Crate.jpg" height="96px" align="right">

Introduces texturing and uv-coordinates on a simple box.

## [TexWaves](Samples/TexWaves)
<img src="./Images/TexWaves.jpg" height="96px" align="right">

Introduces texture animations by animating water texture.

## [TexColumns](Samples/TexColumns)
<img src="./Images/TexColumns.jpg" height="96px" align="right">

Renders the shapes scene with fully textured objects.

## [Blend](Samples/Blend)
<img src="./Images/Blend.jpg" height="96px" align="right">

Introduces the blending formula and how to configure a blend state in the graphics pipeline. Renders the hills and water scene with transparent water and box texture.

## [Stencil](Samples/Stencil)
<img src="./Images/Stencil.jpg" height="96px" align="right">

Constructs a mirror using stencil buffer. Introduces stenciling, projecting mirrored images and shadows.

## [TreeBillboards](Samples/TreeBillboards)
<img src="./Images/TreeBillboards.jpg" height="96px" align="right">

Renders trees as billboards. Introduces alpha to coverage.

## [WavesCS](Samples/WavesCS)

Uses compute shader to update blend sample waves simulation.

## [VecAdd](Samples/VecAdd)

WIP . . .

## [Blur](Samples/Blur)

WIP . . .

## [SobelFilter](Samples/SobelFilter)

WIP . . .

## [BasicTessellation](Samples/BasicTessellation)

WIP . . .

## [BezierPatch](Samples/BezierPatch)

WIP . . .