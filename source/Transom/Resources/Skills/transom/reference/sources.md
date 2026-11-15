# Sources

The build order and habits these skills follow, and where they come from (from the setup runbook of the first project).


Training material and Autodesk documentation agree with this order and these habits. Summaries below are in our own words.

| Practice | Used in | Source |
|---|---|---|
| Levels and key views first, exterior shell, then floors, then roof, then doors and windows, with warnings reviewed at each milestone | Build order | [Revit Essentials for Beginners, modeling sequence and QC](https://cursa.app/en/page/quality-control-and-modeling-sequence-a-repeatable-workflow-for-small-buildings-in-revit) |
| Draw exterior walls on Finish Face: Exterior, chained, then check in 3D | Phase 10 | [Autodesk Getting Started, Create Walls](https://help.autodesk.com/cloudhelp/2022/ENU/Revit-GetStarted/files/GUID-30D3A26F-42FF-40FA-83D2-B43EA67F3ECD.htm) |
| Tie heights to levels, keep offsets for real reasons, make corners truly meet, check joins in section too | Phase 10 | [Clean joins and correct constraints](https://cursa.app/en/page/modeling-exterior-and-interior-walls-best-practices-for-clean-joins-and-correct-constraints) |
| A location line stays put when the type changes, and core-based lines need a correctly defined core | Phase 10 | [Autodesk, About the Wall Location Line](https://help.autodesk.com/cloudhelp/2020/ENU/Revit-Model/files/GUID-F26DB6DA-A0EC-424D-B656-3BDF47607F4F.htm) |
| Layer functions set join priority, and wrapping is set at ends and inserts | Phase 10 | [Autodesk, layer function and priority](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-Model/files/GUID-1CCD1005-CBDA-4338-8D60-489095D4BB25.htm), [editing layers](https://help.autodesk.com/cloudhelp/2025/ENU/Revit-Model/files/GUID-B6EEDFEF-641E-409A-8C42-AB5A66BDC554.htm) |
| Walls do not follow roofs until attached, and attach after the host is stable | Phase 12 | [Autodesk, attach walls](https://help.autodesk.com/cloudhelp/2020/ENU/Revit-Model/files/GUID-FC07B0C8-E2BC-4A37-8131-55C9B7EDED2A.htm) |
| Joining roofs and the attach error | Phase 12 | [Autodesk, join roofs](https://help.autodesk.com/cloudhelp/2020/ENU/Revit-Model/files/GUID-C1B7C157-5E55-4AF3-BBA7-B6FF8EBA7AF0.htm), [Can't keep wall and target joined](https://autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/PTB/Can-t-keep-wall-and-target-joined-when-trying-to-attach-a-wall-to-a-roof-in-Revit.html) |
| Fascia and soffit tools | Phase 12 | [Autodesk, adding roof fascia](https://help.autodesk.com/cloudhelp/2014/ENU/Revit/files/GUID-AC61490D-F98E-481F-8779-FC14AF912E95.htm) |
| Materials through types, not paint, for Enscape | Phase 10 | [Revit to Enscape workflow](https://oastechnologies.com/?p=1561) |
| One full-height wall where type and location match | Rule 3 | [AUGI, exterior walls on multi-floor buildings](https://forums.augi.com/archive/index.php/t-52674.html). Practice is split: others keep one wall per floor for quantities and IFC exports. |

For people learning to model, listed but not reviewed here because they could not be opened or are paid:
[Wall layout, design, and editing with Revit](https://www.autodesk.com/learn/ondemand/curated/wall-layout-design-and-editing-with-revit/1JBHZeeIJLWSNGab1TFLwt) and
[Roof layout, design, and editing in Revit](https://www.autodesk.com/learn/ondemand/curated/roof-layout-design-and-editing-in-revit/4gbqUAuv20FwVWg4i0GmAd) (Autodesk Learn),
[Revit: Multifamily Housing](https://www.lynda.com/Revit-tutorials/Exterior-wall/697716/2219577-4.html) (LinkedIn Learning).
