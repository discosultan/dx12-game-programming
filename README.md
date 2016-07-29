# What is this sorcery?

A collection of *DirectX 12 C# samples* from Frank D. Luna's book [Introduction to 3D Game Programming with Direct3D 12.0](http://d3dcoder.net/d3d12.htm). Samples have been ported to the .NET environment using [SharpDX](http://sharpdx.org/).

This repo contains all the samples from part 2 of the book - *Direct3D foundations*. There are no plans to port samples from part 3.

# Building

All the samples will compile with Visual Studio 2015+ and run on Windows 10 with DirectX 12 capable graphics hardware.

# Samples

## [InitDirect3D](Samples/InitDirect3D)
<img src="./Images/InitDirect3D.jpg" height="96px" align="right">

Sets up a basic Direct3D 12 enabled window. Introduces initializing Direct3D, setting up a game loop, building a base framework upon which next samples are built.

## [Box](Samples/Box)
<img src="./Images/Box.jpg" height="96px" align="right">

Renders a colored box. Introduces vertices and input layouts, vertex and index buffers, programmable vertex and pixel shaders, constant buffers, compiling shaders, rasterizer state, pipeline state object. 

## [Shapes](Samples/Shapes)
<img src="./Images/Shapes.jpg" height="96px" align="right">

Renders multiple objects in a scene. Introduces using multiple world transformation matrices, drawing multiple objects from a single vertex and index buffer.

## [LandAndWaves](Samples/LandAndWaves)
<img src="./Images/LandAndWaves.jpg" height="96px" align="right">

Constructs a basic terrain and animated water geometry. Introduces dynamic vertex buffers.

## [LitWaves](Samples/LitWaves)
<img src="./Images/LitWaves.jpg" height="96px" align="right">

Adds lighting to the previous hills scene. Introduces diffuse, ambient and specular lighting, materials and directional lights. 

## [LitColumns](Samples/LitColumns)
<img src="./Images/LitColumns.jpg" height="96px" align="right">

Introduces parsing and loading a skeleton model mesh from a custom model format. Applies lighting to the shapes scene.

## [Crate](Samples/Crate)
<img src="./Images/Crate.jpg" height="96px" align="right">

Introduces texturing and uv-coordinates on a simple box.
<br><br>

## [TexWaves](Samples/TexWaves)
<img src="./Images/TexWaves.jpg" height="96px" align="right">

Introduces texture animations by animating the water texture in the hills scene.
<br><br>

## [TexColumns](Samples/TexColumns)
<img src="./Images/TexColumns.jpg" height="96px" align="right">

Renders the shapes scene with fully textured objects.
<br><br>

## [Blend](Samples/Blend)
<img src="./Images/Blend.jpg" height="96px" align="right">

Renders the hills scene with transparent water and a wire fence box texture. Introduces the blending formula, how to configure a blend state in the graphics pipeline and how to create a fog effect.

## [Stencil](Samples/Stencil)
<img src="./Images/Stencil.jpg" height="96px" align="right">

Constructs a mirror using stencil buffer. Introduces stenciling, projecting mirrored images and rendering shadows.

## [TreeBillboards](Samples/TreeBillboards)
<img src="./Images/TreeBillboards.jpg" height="96px" align="right">

Renders trees as billboards. Introduces texture arrays and alpha to coverage in relation to MSAA.
<br><br>

## [VecAdd](Samples/VecAdd)

Sums a bunch of vectors on GPU instead of CPU for high parallelism. Introduces programmable compute shaders. Outputs a 'results.txt' file instead of rendering to screen.

## [WavesCS](Samples/WavesCS)
<img src="./Images/Blend.jpg" height="96px" align="right">

Uses compute shader to update the hills scene waves simulation on GPU instead of CPU.
<br><br>

## [Blur](Samples/Blur)
<img src="./Images/Blur.jpg" height="96px" align="right">

Applies a Gaussian blur post processing effect using compute shader to the hills scene. Introduces render targets. 

## [SobelFilter](Samples/SobelFilter)
<img src="./Images/SobelFilter.jpg" height="96px" align="right">

Applies a sobel filter post processing effect using compute shader to the hills scene to render strong outlines for geometry.

## [BasicTessellation](Samples/BasicTessellation)
<img src="./Images/BasicTessellation.jpg" height="96px" align="right">

Tessellates a quad using 4 control points. Introduces programmable hull and domain shaders and the fixed tessellator stage. 

## [BezierPatch](Samples/BezierPatch)
<img src="./Images/BezierPatch.jpg" height="96px" align="right">

Tessellates a quad using 16 control points cubic Bézier surface.
