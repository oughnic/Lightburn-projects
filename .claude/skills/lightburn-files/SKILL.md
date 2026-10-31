---
name: lightburn-files
description: Create LightBurn project files (.lbrn2) for a Creality Falcon 2 laser cutter by generating them from code. Use when asked to design laser-cut parts, boxes or finger joints, write or change a .lbrn2 generator, add a new project to this repo, or fix files that LightBurn loads wrongly (missing shapes, stray diagonal cuts).
---

# Creating LightBurn files

This repo generates LightBurn `.lbrn2` files from code instead of drawing them by hand.
The working example is `drawer-boxes/generate_boxes.cs`. Read its writer section before
writing a new one. Everything below was learned by testing against LightBurn 2.1.04.

## Workflow for a new project

1. Work on a branch and merge through a pull request. Never commit straight to main.
2. Make a new top-level folder for the project with its own `README.md`, then add a row
   for it to the table in the root `README.md`.
3. Write the generator as a single-file C# script, for example `generate_thing.cs`. Run it
   with `dotnet run generate_thing.cs` (.NET 10 SDK or later). There is no Python or Node
   on the target machine, so do not reach for them.
4. Put the constants at the top: material thickness, bed size, margin, gap, laser settings.
   Dimensions the user gives are external unless they say otherwise.
5. Emit one `.lbrn2` per design plus an SVG preview per design in a `preview/` folder.
   Look at every preview before saying the geometry is right.
6. Open at least one generated file in LightBurn to confirm it loads (see Verifying).
7. Start each source file with the repo licence header:
   `// Copyright 2026 Nicholas Oughtibridge` and `// SPDX-License-Identifier: Apache-2.0`.

## Machine and defaults

- Creality Falcon 2 bed: 400 x 415 mm.
- Keep parts 10 mm inside the bed edge, so usable area is 380 x 395 mm.
- Leave 4 mm between parts. Allow rotation by 90 degrees when packing.
- Starting layer values: 8 mm/s, 100 % power, 1 pass, 0.1 mm kerf. These are untested
  guesses for a 22 W Falcon 2 on 4 mm ply. Say so in the README and the file notes, and
  tell the user to cut a scrap test first.

## File structure

```xml
<?xml version="1.0" encoding="UTF-8"?>
<LightBurnProject AppVersion="1.7.08" FormatVersion="1" MaterialHeight="4" MirrorX="False" MirrorY="False">
    <CutSetting type="Cut">
        <index Value="0"/>
        <name Value="Sheet 1"/>
        <minPower Value="100"/>
        <maxPower Value="100"/>
        <maxPower2 Value="100"/>
        <speed Value="8"/>
        <numPasses Value="1"/>
        <kerf Value="0.1"/>
        <priority Value="0"/>
    </CutSetting>
    <Shape Type="Path" CutIndex="0" VertID="0" PrimID="0">
        <XForm>1 0 0 1 0 0</XForm>
        <VertList>V10 28.571c0x1c1x1V14 28.571c0x1c1x1 ...</VertList>
        <PrimList>LineClosed</PrimList>
    </Shape>
    <Notes ShowOnLoad="1" Notes="Text shown in a dialog when the file opens.&#10;Second line."/>
</LightBurnProject>
```

- Coordinates are millimetres. Write numbers with the invariant culture and a dot decimal
  point, to at most three decimals.
- One `CutSetting` per layer. A shape's `CutIndex` picks its layer by `index`.
- Each vertex is `V<x> <y>c0x1c1x1`, joined with no separators. `PrimList` of `LineClosed`
  makes a closed polyline through them. Only closed polylines have been tested. Curves and
  other primitives are untested, so check them in LightBurn before relying on them.
- Remove collinear points before writing, so straight edges are one segment.
- Escape `&`, `<`, `>`, `"` in the notes, and use `&#10;` for newlines.

## Gotchas that cost time

- **Give every Shape unique `VertID` and `PrimID`.** Use a counter. Without them
  LightBurn 2.x silently drops the shape and the file opens empty. With the same id on
  every shape LightBurn merges their vertex lists and draws stray diagonal cuts joining
  the parts. This was the cause of diagonal lines on every side panel.
- **Use the compact `VertList` / `PrimList` form.** The older one-element-per-vertex form
  (`<V vx= vy=/>`, `<P T="L"/>`) loads with no shapes in LightBurn 2.1.
- **Put `<Notes>` last**, just before the closing tag.
- **One file per sheet is easier to use than parked layers.** `drawer-cabinet` writes a file per sheet, each with a single `Cut` layer. The parked-layers approach in `drawer-boxes` is fine for 2 or 3 sheets.
- **Spread work over several sheets when it does not fit.** Give each sheet its own
  `CutSetting` named `Sheet n`, and park sheets after the first at an x offset of
  bed width plus 20 mm. The user drags a sheet onto the bed or disables the other layers.
  Say how many sheets a design needs in the notes and the README.
- **If the format is in doubt, ask LightBurn.** Import an SVG, save it as `.lbrn2`, and
  diff against your output. That is how the points above were found.

## Finger joints

- Model each panel as a full rectangle, then subtract rectangular notches along an edge
  wherever the neighbouring panel owns that stretch of the joint. Trace the outline from a
  grid of occupied cells and chain the boundary segments.
- Pick an odd number of segments per edge, close to a 12 mm target, so both ends of an edge
  are fingers. Divide the edge evenly.
- Alternate ownership by segment parity: for vertical corner joints, even segments go to
  the front and back panels and odd ones to the left and right. For base joints, even go
  to the side and odd to the base. Relieve the base corners so four sides meet cleanly.
- The notch depth equals the material thickness. Kerf is handled in LightBurn, not in the
  geometry.

## Verifying

0. For anything with more than one panel, check the joints in code before writing files.
   Place every panel in 3D, assert that no two overlap, and assert that the total volume
   equals the expected solid. `drawer-cabinet/generate_cabinet.cs` (`Verify`) does this and
   caught a rounding clash straight away. Round every coordinate to 4 decimals when you
   create it, so slots and tabs agree exactly.
1. Compute each shape's perimeter in the generator and sum it. Then open the file in
   LightBurn, press Alt+P for the preview, and compare the cut distance. Agreement within a
   few millimetres means no stray lines. Known good: 2588 mm in LightBurn against 2592 mm
   computed for the 2x3 box, and 2020 mm against 2022 mm for a cabinet sheet.
2. Check the shape count and that every part sits inside the bed margin.
3. When driving LightBurn through computer use, open files with Ctrl+O, click the File name
   box, type the full path, then press Enter as a separate action. The tool refuses
   newlines typed into a file dialog. Do not use `open_application` repeatedly, since it
   starts extra LightBurn instances. Dismiss the update prompt with No. LightBurn marks a
   file modified after Preview, so a save prompt appears when you open another file or
   exit: choose Discard, because Save overwrites the generated file.
4. Tell the user plainly which files were opened in LightBurn and which were only checked
   in the SVG previews.

## README for a project

Include the sizes and file names in a table, the sheet count for each, cutting notes
(margin, layers, untested settings, fit warnings), how to regenerate, and a line on any
LightBurn quirks the project hit. `drawer-boxes/README.md` is the template.
