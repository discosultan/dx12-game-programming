# What is this sorcery?

A collection of *DirectX 12 C# samples* from Frank D. Luna's book [Introduction to 3D Game Programming with Direct3D 12.0](http://d3dcoder.net/d3d12.htm). All the samples have been ported to .NET using [SharpDX](http://sharpdx.org/).

# Building

The samples will compile with Visual Studio 2022+ and run on Windows 10+ with DirectX 12 capable graphics hardware.

# Samples

The number prefix for sample name indicates its corresponding chapter from the book. 

Note that there might be issues: some which are inherited from the original C++ samples and some which have been introduced during the porting process. All known issues are listed in the [issues section](https://github.com/discosultan/dx12-game-programming/issues).

## [04-InitDirect3D](Samples/04-InitDirect3D)
<img src="./Images/InitDirect3D.jpg" height="96px" align="right">

Sets up a window using WinForms. Initializes Direct3D 12 and builds a base app with game loop upon which next samples are built.

## [06-Box](Samples/06-Box)
<img src="./Images/Box.jpg" height="96px" align="right">

Manually defines vertices to render a colored box. Scene can be rotated and zoomed using mouse buttons.

## [07-Shapes](Samples/07-Shapes)
<img src="./Images/Shapes.jpg" height="96px" align="right">

Generates geometric primitives. Renders multiple objects using a single vertex and index buffer.
<br><br>

## [07-LandAndWaves](Samples/07-LandAndWaves)
<img src="./Images/LandAndWaves.jpg" height="96px" align="right">

Constructs a heightmap based terrain and water geometry using a dynamic vertex buffer.
<br><br>

## [08-LitWaves](Samples/08-LitWaves)
<img src="./Images/LitWaves.jpg" height="96px" align="right">

Adds ambient, diffuse and specular lighting to the land and waves scene.
<br><br> 

## [08-LitColumns](Samples/08-LitColumns)
<img src="./Images/LitColumns.jpg" height="96px" align="right">

Introduces parsing and loading a skull mesh from a custom model format. Applies lighting to the shapes scene.

## [09-Crate](Samples/09-Crate)
<img src="./Images/Crate.jpg" height="96px" align="right">

Textures a box with uv-coordinates added to its vertices.
<br><br>

## [09-TexWaves](Samples/09-TexWaves)
<img src="./Images/TexWaves.jpg" height="96px" align="right">

Animates a water texture in the land and waves scene.
<br><br>

## [09-TexColumns](Samples/09-TexColumns)
<img src="./Images/TexColumns.jpg" height="96px" align="right">

Textures objects in the shapes scene.
<br><br>

## [10-Blend](Samples/10-Blend)
<img src="./Images/Blend.jpg" height="96px" align="right">

Renders the land and waves scene with transparent water and a wire fence textures. Introduces the blending formula and creates a fog effect.

## [11-Stencil](Samples/11-Stencil)
<img src="./Images/Stencil.jpg" height="96px" align="right">

Constructs a mirror using the stencil buffer and projects a shadow for the skull mesh.
<br><br>

## [12-TreeBillboards](Samples/12-TreeBillboards)
<img src="./Images/TreeBillboards.jpg" height="96px" align="right">

Renders trees as billboards. Introduces texture arrays and alpha to coverage in relation to MSAA.
<br><br>

## [13-VecAdd](Samples/13-VecAdd)

Sums a bunch of vectors on GPU instead of CPU for high parallelism using a compute shader. Outputs a *results.txt* file instead of rendering to screen.

## [13-WavesCS](Samples/13-WavesCS)
<!-- This sample looks exactly the same as 10-Blend -->
<img src="./Images/Blend.jpg" height="96px" align="right">

Uses a compute shader to update the land and waves scene waves simulation on GPU instead of CPU.

## [13-Blur](Samples/13-Blur)
<img src="./Images/Blur.jpg" height="96px" align="right">

Applies a Gaussian blur post-processing effect using a compute shader to the land and waves scene.

## [13-SobelFilter](Samples/13-SobelFilter)
<img src="./Images/SobelFilter.jpg" height="96px" align="right">

Applies a Sobel filter post-processing effect using a compute shader to the land and waves scene to render outlines for geometry.

## [14-BasicTessellation](Samples/14-BasicTessellation)
<img src="./Images/BasicTessellation.jpg" height="96px" align="right">

Tessellates a quad using 4 control points.
<br><br>

## [14-BezierPatch](Samples/14-BezierPatch)
<img src="./Images/BezierPatch.jpg" height="96px" align="right">

Tessellates a quad using 16 control points cubic Bézier surface.
<br><br>

## [15-CameraAndDynamicIndexing](Samples/15-CameraAndDynamicIndexing)
<img src="./Images/CameraAndDynamicIndexing.jpg" height="96px" align="right">

Creates a controllable first person camera. Introduces dynamic indexing of texture arrays. Camera is moved using WASD keys and rotated using a mouse.

## [16-InstancingAndCulling](Samples/16-InstancingAndCulling)
<img src="./Images/InstancingAndCulling.jpg" height="96px" align="right">

Renders multiple copies of the skull mesh using a hardware instanced draw call. Culls skulls outside of camera frustum.

## [17-Picking](Samples/17-Picking)
<img src="./Images/Picking.jpg" height="96px" align="right">

Enables picking individual triangles of a car mesh. Right mouse button picks a triangle which is highlighted using a yellow color.

## [18-CubeMap](Samples/18-CubeMap)
<img src="./Images/CubeMap.jpg" height="96px" align="right">

Renders a sky texture cube. Uses the cube to render reflections on scene objects.
<br><br>

## [18-DynamicCube](Samples/18-DynamicCube)
<img src="./Images/DynamicCube.jpg" height="96px" align="right">

Renders scene objects into a texture cube every frame. Uses the rendered cube for reflections.
<br><br>

## [19-NormalMap](Samples/19-NormalMap)
<img src="./Images/NormalMap.jpg" height="96px" align="right">

Makes use of normal maps in addition to diffuse maps to generate more realistic lighting of surfaces.

## [20-Shadows](Samples/20-Shadows)
<img src="./Images/Shadows.jpg" height="96px" align="right">

Projects shadows into a shadow map which is blended with the diffuse target.
<br><br>

## [21-Ssao](Samples/21-Ssao)
<img src="./Images/Ssao.jpg" height="96px" align="right">

Computes real-time screen space ambient occlusion and applies it as a post-processing effect.
<br><br>

## [22-Quaternions](Samples/22-Quaternions)
<img src="./Images/Quaternions.jpg" height="96px" align="right">

Animates skull rotation using quaternions.
<br><br>

## [23-SkinnedMesh](Samples/23-SkinnedMesh)
<img src="./Images/SkinnedMesh.jpg" height="96px" align="right">

Plays a walking animation for an animated skinned soldier mesh loaded from *.m3d* format.
