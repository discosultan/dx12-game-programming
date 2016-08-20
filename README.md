# What is this sorcery?

A collection of *DirectX 12 C# samples* from Frank D. Luna's book [Introduction to 3D Game Programming with Direct3D 12.0](http://d3dcoder.net/d3d12.htm). All the samples have been ported to .NET using [SharpDX](http://sharpdx.org/).

# Building

The samples will compile with Visual Studio 2015+ and run on Windows 10 with DirectX 12 capable graphics hardware.

# Samples

## [04-InitDirect3D](Samples/04-InitDirect3D)
<img src="./Images/InitDirect3D.jpg" height="96px" align="right">

Sets up a basic Direct3D 12 enabled window. Introduces initializing Direct3D, setting up a game loop, building a base framework upon which next samples are built.

## [06-Box](Samples/06-Box)
<img src="./Images/Box.jpg" height="96px" align="right">

Renders a colored box. Introduces vertices and input layouts, vertex and index buffers, programmable vertex and pixel shaders, constant buffers, compiling shaders, rasterizer state, pipeline state object. 

## [07-Shapes](Samples/07-Shapes)
<img src="./Images/Shapes.jpg" height="96px" align="right">

Renders multiple objects in a scene. Introduces using multiple world transformation matrices, drawing multiple objects from a single vertex and index buffer.

## [07-LandAndWaves](Samples/07-LandAndWaves)
<img src="./Images/LandAndWaves.jpg" height="96px" align="right">

Constructs a basic terrain and animated water geometry. Introduces dynamic vertex buffers.

## [08-LitWaves](Samples/08-LitWaves)
<img src="./Images/LitWaves.jpg" height="96px" align="right">

Adds lighting to the previous hills scene. Introduces diffuse, ambient and specular lighting, materials and directional lights. 

## [08-LitColumns](Samples/08-LitColumns)
<img src="./Images/LitColumns.jpg" height="96px" align="right">

Introduces parsing and loading a skeleton model mesh from a custom model format. Applies lighting to the shapes scene.

## [09-Crate](Samples/09-Crate)
<img src="./Images/Crate.jpg" height="96px" align="right">

Introduces texturing and uv-coordinates on a simple box.
<br><br>

## [09-TexWaves](Samples/09-TexWaves)
<img src="./Images/TexWaves.jpg" height="96px" align="right">

Introduces texture animations by animating the water texture in the hills scene.
<br><br>

## [09-TexColumns](Samples/09-TexColumns)
<img src="./Images/TexColumns.jpg" height="96px" align="right">

Renders the shapes scene with fully textured objects.
<br><br>

## [10-Blend](Samples/10-Blend)
<img src="./Images/Blend.jpg" height="96px" align="right">

Renders the hills scene with transparent water and a wire fence box texture. Introduces the blending formula, how to configure a blend state in the graphics pipeline and how to create a fog effect.

## [11-Stencil](Samples/11-Stencil)
<img src="./Images/Stencil.jpg" height="96px" align="right">

Constructs a mirror using stencil buffer. Introduces stenciling, projecting mirrored images and rendering shadows.

## [12-TreeBillboards](Samples/12-TreeBillboards)
<img src="./Images/TreeBillboards.jpg" height="96px" align="right">

Renders trees as billboards. Introduces texture arrays and alpha to coverage in relation to MSAA.
<br><br>

## [13-VecAdd](Samples/13-VecAdd)

Sums a bunch of vectors on GPU instead of CPU for high parallelism. Introduces programmable compute shaders. Outputs a 'results.txt' file instead of rendering to screen.

## [13-WavesCS](Samples/13-WavesCS)
<img src="./Images/Blend.jpg" height="96px" align="right">

Uses compute shader to update the hills scene waves simulation on GPU instead of CPU.
<br><br>

## [13-Blur](Samples/13-Blur)
<img src="./Images/Blur.jpg" height="96px" align="right">

Applies a Gaussian blur post processing effect using compute shader to the hills scene. Introduces render targets. 

## [13-SobelFilter](Samples/13-SobelFilter)
<img src="./Images/SobelFilter.jpg" height="96px" align="right">

Applies a sobel filter post processing effect using compute shader to the hills scene to render strong outlines for geometry.

## [14-BasicTessellation](Samples/14-BasicTessellation)
<img src="./Images/BasicTessellation.jpg" height="96px" align="right">

Tessellates a quad using 4 control points. Introduces programmable hull and domain shaders and the fixed tessellator stage. 

## [14-BezierPatch](Samples/14-BezierPatch)
<img src="./Images/BezierPatch.jpg" height="96px" align="right">

Tessellates a quad using 16 control points cubic Bézier surface.

## [15-CameraAndDynamicIndexing](Samples/15-CameraAndDynamicIndexing)
<img src="./Images/CameraAndDynamicIndexing.jpg" height="96px" align="right">

WIP

## [16-InstancingAndCulling](Samples/16-InstancingAndCulling)
<img src="./Images/InstancingAndCulling.jpg" height="96px" align="right">

WIP

## [17-Picking](Samples/17-Picking)
<img src="./Images/Picking.jpg" height="96px" align="right">

WIP

## [18-CubeMap](Samples/18-CubeMap)
<img src="./Images/CubeMap.jpg" height="96px" align="right">

WIP

## [18-DynamicCube](Samples/18-DynamicCube)
<img src="./Images/DynamicCube.jpg" height="96px" align="right">

WIP

## [19-NormalMap](Samples/19-NormalMap)
<img src="./Images/NormalMap.jpg" height="96px" align="right">

WIP

## [20-Shadows](Samples/20-Shadows)
<img src="./Images/Shadows.jpg" height="96px" align="right">

WIP

## [21-Ssao](Samples/21-Ssao)
<img src="./Images/Ssao.jpg" height="96px" align="right">

WIP

## [22-Quaternions](Samples/22-Quaternions)
<img src="./Images/Quaternions.jpg" height="96px" align="right">

WIP

## [23-SkinnedMesh](Samples/23-SkinnedMesh)
<img src="./Images/SkinnedMesh.jpg" height="96px" align="right">

WIP
