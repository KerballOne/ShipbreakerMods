"""One-time move of a BayLights 1.0 config to 1.1 (whitelist).  Keeps a .bak copy next to the file.

1.0 had [ShipLights] with multipliers for every Area / Point / Spot light on a ship.  1.1 changes only named groups:
  [SodiumLights]  takes the old Area multipliers and the old DisableShadows value
  the old Point and Spot multipliers have no successor (those lights are left alone unless a [Custom] rule names them)
Usage: python migrate_config.py [path to me.kerballone.BayLights.cfg]"""
import re
import shutil
import sys

p = sys.argv[1] if len(sys.argv) > 1 else r'C:\Program Files (x86)\Steam\steamapps\common\Hardspace Shipbreaker\BepInEx\config\me.kerballone.BayLights.cfg'
s = open(p, encoding='utf-8').read()
if '[SodiumLights]' in s:
    sys.exit('already migrated')
shutil.copyfile(p, p + '.bak')


def old(key, default):
    m = re.search(r'(?ms)^\[ShipLights\].*?^%s = (.*?)$' % key, s)
    return m.group(1).strip() if m else default


sodium = ('[SodiumLights]\n\nEnabled = true\n\nNamePatterns = *Sodium*\n\nIntensityMultiplier = %s\n\nRangeMultiplier = %s\n\nDisableShadows = %s\n\n'
          % (old('AreaIntensityMultiplier', '5'), old('AreaRangeMultiplier', '2'), old('DisableShadows', 'true')))
dropped = {k: old(k, '?') for k in ('PointIntensityMultiplier', 'PointRangeMultiplier', 'SpotIntensityMultiplier', 'SpotRangeMultiplier', 'ExcludeNameContains')}
s = re.sub(r'(?ms)^\[ShipLights\].*?(?=^\[|\Z)', '', s)
s = s.rstrip('\n') + '\n\n' + sodium + '[Custom]\n\nRules = \n\nDisableShadows = false\n'
open(p, 'w', encoding='utf-8').write(s)
print('sodium section written:', sodium.replace('\n\n', ' | ').strip(' |'))
print('old settings with no successor:', dropped)
