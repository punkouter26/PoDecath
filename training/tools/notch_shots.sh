#!/usr/bin/env bash
# Every screen at the four portrait shapes phones ship in, with a simulated notch and home bar, from a
# running Unity editor. The Game view reports the whole screen as safe, so the notch is faked through
# UiRoot.SafeAreaOverride (UiShots.Notch) and the check is UiShots.NotchCheck: every visible button, and
# the results modal, against the frame's rows and against the band a phone can actually show.
#
#   training/tools/notch_shots.sh <prefix> [notchTopPx] [homeBarPx]
#
# Defaults are the worst case on sale: a 47 pt notch and a 34 pt home bar at 3x (141 / 102 px at 1080 wide).
# A field of sixteen, because that is what makes the results card tallest. One race; the Game view is
# re-sized under each screen rather than running a race per shape. Writes Build/UiShots/<prefix>_*.png and
# prints one layout line and one notch line per picture.
set -u
PREFIX="${1:-notch}"
TOP="${2:-141}"
BOTTOM="${3:-102}"
SHAPES="1920 2340 2400 2520"   # 16:9, 19.5:9, 20:9, 21:9
cd "$(dirname "$0")/../.."

ev() {  # one C# expression in the editor; a reading that hit the 5 s main-thread limit is tried again
  local out i
  for i in 1 2 3; do
    out=$(timeout 90 unity command eval --code "if (UnityEngine.Application.isEditor) return $1; return null;" 2>&1 | tail -1)
    case "$out" in *"timed out"*) sleep 2 ;; *) break ;; esac
  done
  echo "$out" \
    | sed -E 's/.*"result":("[^"]*"|[^,}]*).*/\1/' | sed -E 's/^"(.*)"$/\1/' | cut -c1-2000
}
step() { local n=$1; while [ "$n" -gt 0 ]; do ev "PoDecath.EditorTools.UiShots.Step(10)" >/dev/null; n=$((n-10)); done; }
call() { ev "PoDecath.EditorTools.UiShots.Call(\"$1\",\"$2\"${3:+, $3})"; }
every_shape() {  # every_shape <screen>: picture + both checks at each shape, notch on
  for H in $SHAPES; do
    ev "PoDecath.EditorTools.UiShots.SetGameViewSize(1080, $H, \"PoDecath 1080x$H\")" >/dev/null
    step 3
    ev "PoDecath.EditorTools.UiShots.Notch($TOP, $BOTTOM)" >/dev/null
    step 6
    ev "PoDecath.EditorTools.UiShots.Capture(\"${PREFIX}_$1_$H\", 1080, $H)" >/dev/null
    echo "$1 1080x$H: $(ev "PoDecath.EditorTools.UiShots.CheckLayout().Summary().Split('\n')[0]" | sed 's/.*elements, //')"
    echo "      $(ev "PoDecath.EditorTools.UiShots.NotchCheck()" | sed 's/\\n/\n        /g')"
  done
  ev "PoDecath.EditorTools.UiShots.SetGameViewSize(1080, 1920, \"PoDecath 9:16\")" >/dev/null
  step 3
}

unity command editor_stop >/dev/null 2>&1
unity command open_scene --path Assets/Scenes/MAIN.unity >/dev/null
unity command menu --path "PoDecath/UI/Game View 1080x1920 (9:16)" >/dev/null
unity command editor_play >/dev/null
sleep 3
step 30

call SetupView SetEveryone 2 >/dev/null   # two of each: the full sixteen
call SetupView SelectEvent 0 >/dev/null
every_shape setup

call SetupView StartRace >/dev/null
step 40
ev 'UnityEngine.Time.timeScale = 6f' >/dev/null
step 300
ev 'UnityEngine.Time.timeScale = 1f' >/dev/null
every_shape race

ev 'UnityEngine.Time.timeScale = 6f' >/dev/null
for i in $(seq 1 200); do
  step 15
  case "$(ev 'PoDecath.EditorTools.UiShots.State()')" in *"results ON"*) break;; esac
done
ev 'UnityEngine.Time.timeScale = 1f' >/dev/null
step 20
every_shape results

# FIELD: what the menu comes back with after a race of sixteen.
call ResultsView ChangeRunners >/dev/null
step 20
ev "PoDecath.EditorTools.UiShots.Notch(0, 0)" >/dev/null
step 6
ev "PoDecath.EditorTools.UiShots.Capture(\"${PREFIX}_field_return\", 1080, 1920)" >/dev/null
echo "after FIELD: $(ev 'PoDecath.EditorTools.UiShots.State()')"

ev "PoDecath.EditorTools.UiShots.Notch(0, 0)" >/dev/null
unity command editor_stop >/dev/null
ev 'UnityEngine.Time.timeScale = 1f' >/dev/null
unity command menu --path "PoDecath/UI/Game View 1080x1920 (9:16)" >/dev/null
echo "Pictures in Build/UiShots/${PREFIX}_*.png"
