import argparse, os
from isaaclab.app import AppLauncher
p = argparse.ArgumentParser(); AppLauncher.add_app_launcher_args(p); a = p.parse_args(); a.headless = True
app = AppLauncher(a).app
from pxr import Usd, UsdPhysics, UsdGeom
path = os.path.abspath("usd/athlete.usd")
stage = Usd.Stage.Open(path)
roots, bodies, geoms = [], [], []
for prim in stage.Traverse():
    if prim.HasAPI(UsdPhysics.ArticulationRootAPI): roots.append(prim.GetPath().pathString)
    if prim.HasAPI(UsdPhysics.RigidBodyAPI): bodies.append(prim.GetPath().pathString)
    if prim.IsA(UsdGeom.Gprim): geoms.append(prim.GetPath().pathString)
print("FIX roots before:", roots)
print("FIX rigid bodies:", len(bodies), bodies[:16])
print("FIX geoms:", len(geoms), [g for g in geoms if "floor" in g.lower() or "world" in g.lower()][:6])
# keep the pelvis root, drop the worldBody wrapper root; deactivate any imported floor/plane
for r in roots:
    prim = stage.GetPrimAtPath(r)
    if r.endswith("/worldBody") or "worldBody" in r and not r.endswith("pelvis"):
        prim.RemoveAPI(UsdPhysics.ArticulationRootAPI)
        print("FIX removed ArticulationRootAPI from", r)
for g in geoms:
    if "floor" in g.lower():
        stage.GetPrimAtPath(g).SetActive(False); print("FIX deactivated", g)
# the edits land in the strongest layer that defines them; save all layers
for layer in stage.GetUsedLayers():
    if not layer.anonymous and layer.dirty if hasattr(layer, 'dirty') else True:
        try: layer.Save()
        except Exception as e: print("FIX layer save skipped", layer.identifier, e)
stage.GetRootLayer().Save()
roots2 = [pr.GetPath().pathString for pr in Usd.Stage.Open(path).Traverse() if pr.HasAPI(UsdPhysics.ArticulationRootAPI)]
print("FIX roots after:", roots2)
app.close()
