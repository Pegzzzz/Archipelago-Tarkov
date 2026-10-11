#!/bin/bash
# Runs the client plugin's game-access logic under Mono against stand-in game assemblies.
set -u
# CLIENT_REFS: a folder with BepInEx.dll and spt-common.dll (from SPT 4.0.13)
R=${CLIENT_REFS:?set CLIENT_REFS to a folder with BepInEx.dll and spt-common.dll}
P=${PLUGIN_DIR:-$(cd "$(dirname "$0")/../.." && pwd)/client-plugin/ArchipelagoTarkov.Client/bin/Release}
NS=/usr/lib/mono/4.5/Facades/netstandard.dll
cd "$(dirname "$0")"
total=0
for mode in kill killme none; do
  d=run_$mode; rm -rf $d; mkdir -p $d
  defs=""; [ $mode = kill ] && defs="-d:WITH_KILL"; [ $mode = killme ] && defs="-d:WITH_KILLME"
  mcs -nologo -target:library -out:$d/Comfort.dll stub_comfort.cs || exit 1
  mcs -nologo -target:library -out:$d/UnityEngine.CoreModule.dll stub_unity.cs || exit 1
  mcs -nologo -target:library $defs -out:$d/Assembly-CSharp.dll -r:$d/UnityEngine.CoreModule.dll stub_game.cs || exit 1
  mcs -nologo -out:$d/runner.exe runner.cs || exit 1
  cp $P/ArchipelagoTarkov.Client.dll $R/BepInEx.dll $R/spt-common.dll $d/
  (cd $d && mono runner.exe $mode); rc=$?
  total=$((total + rc))
done
echo "plugin logic failures: $total"
exit $total
