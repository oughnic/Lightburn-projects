# Kitchen drawer divider boxes

Modular open-top boxes for an 880 x 440 mm drawer, on a 110 mm grid.
All boxes are 60 mm tall, cut from 4 mm sheet, and all dimensions are **external**.

| File            | Footprint (mm) | Sheets on a Falcon 2 (400 x 415) |
|-----------------|----------------|----------------------------------|
| `box_1x1.lbrn2` | 110 x 110      | 1                                |
| `box_1x2.lbrn2` | 110 x 220      | 1                                |
| `box_1x3.lbrn2` | 110 x 330      | 1                                |
| `box_2x3.lbrn2` | 220 x 330      | 2                                |
| `box_3x3.lbrn2` | 330 x 330      | 2                                |

Each file holds five parts: one base, two "Side A" panels and two "Side B" panels.
Side A has tabs at its top corners, Side B has notches there, so the two types alternate
around the box. The `preview/` folder has an SVG picture of every layout.

## Cutting notes

- Parts are kept 10 mm inside the bed edges. Layers are named `Sheet 1` (and `Sheet 2` for
  the 2x3 and 3x3). Sheet 2 parts are parked to the
  right of the bed; drag them onto the bed for the second run, or disable the layer you are
  not cutting.
- Layer defaults are 8 mm/s, 100 % power, 1 pass, 0.1 mm kerf offset. These are only a
  starting point for a 22 W Falcon 2 on 4 mm ply. Cut a small test first and adjust speed,
  passes and kerf: a kerf offset that is too small gives loose fingers, too large and they
  will not go together.
- Eight 1x1 boxes across the drawer add up to exactly 880 mm. If the drawer is a tight
  880 mm internally, consider a 109.5 mm unit, or leave one gap.

## Regenerating

`generate_boxes.cs` is a single-file C# script (needs the .NET 10+ SDK). Edit the constants
at the top (material thickness, height, unit, finger width, laser settings) and run:

```bash
dotnet run generate_boxes.cs
```

It rewrites the `.lbrn2` files and the SVG previews.
