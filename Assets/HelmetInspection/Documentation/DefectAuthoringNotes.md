# Defect authoring record

## Source integrity

- `A1_helmet.glb` SHA-256: `D5F2D42979D4ED9895BA35A3161F9DFAC30ABC672E5E7FC9994E74AF5F7FD3C5`
- `A2_defective_scan.glb` SHA-256: `53341635C4017E4CA4FBE2C42D5B29CAF7636CF623D66BA55748D9EFFDF7B0D8`
- Both source scans are single-node, single-mesh GLB 2 files with no UVs, skeletons, or animation.
- Source coordinates are millimetres. The importer converts the source Z-up basis to Unity Y-up and applies `0.001` exactly once, producing helmet-scale native Unity meshes.

## Reproducible comparison

`DefectAuthoringTool` builds a bounding-volume hierarchy over every A1 triangle. For every A2 vertex it calculates the exact closest point on an A1 triangle, not merely the nearest A1 vertex. Vertices at or above 2 mm deviation become candidates. Mesh-edge adjacency joins connected candidates into surface clusters.

Clusters are ranked by deviation and area proxy, then deterministic non-maximum suppression keeps spatially separate findings. The ten final records are written to `Assets/HelmetInspection/Data/DefectSet_A2.asset`, including position, normal, maximum deviation, severity, source vertex, source cluster size, inspection note, and corrective action. Runtime hotspots read this asset; defect coordinates are not hardcoded in the scene scripts.

The generated validation report records every final location and measurement. A qualified helmet engineer should approve classifications and acceptance limits before the module is used as a real safety certification or pass/fail authority.
