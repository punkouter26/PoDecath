using UnityEditor;
using UnityEngine;

namespace PoDecath.EditorTools
{
    /// <summary>
    /// ProBuilder long jump runway and sand pit on a deck filling the inside of the kart loop.
    ///
    /// The track is a ribbon on legs, so its infield is open air above the residence roof; this builds a
    /// floor across that interior at the same height as the deck, and cuts the pit into it as a genuine
    /// recess rather than laying a sand-coloured slab on top. The track's own inner barrier already rings
    /// the interior, so the infield comes out as an enclosed field.
    ///
    /// Nothing here is a fixed size. The interior is a stadium of half-length <c>halfLength</c> and radius
    /// <c>radius - width/2</c>, and the event is solved backwards into it: the sand ends exactly where the
    /// east end cap begins (which is what makes the pit's far wall a flat face rather than a curve), the
    /// board sits one metre back from the sand, and the runway takes whatever is left. On the roof as
    /// currently built that is a 17.9 m runway and an 8 m pit inside a 34.7 x 12.3 m interior — scaled
    /// down from the regulation 40 m and 9 m, which will not fit on the Executive Residence.
    ///
    /// The event runs <paramref name="lateral"/> metres off the loop's centre line, because the White
    /// House flagpole stands at the exact centre of the residence roof, which is 2 m short of the board.
    /// </summary>
    public static class LongJumpBuilder
    {
        public struct PitInfo
        {
            /// <summary>The event node: the runway's centre line is this transform's local x axis.</summary>
            public Transform root;
            /// <summary>Offset of the runway centre line from the loop's, in the loop's frame.</summary>
            public float lateral;
            public float runwayStartX;   // local x, start of the runway surface
            public float takeoffX;       // local x of the foul line (far edge of the board)
            public float boardDepth;
            public float pitNearX, pitFarX;
            public float runwayWidth, pitWidth;
            public float surfaceY;       // world Y of the infield deck top
            public float sandY;          // world Y of the sand
            public float RunwayLength => takeoffX - runwayStartX;
            public float PitLength => pitFarX - pitNearX;
        }

        const float RunwayWidth = 1.22f;   // regulation
        const float BoardDepth = 0.20f;    // regulation
        const float TakeoffGap = 1.00f;    // board to the near edge of the sand
        const float PitWidth = 2.75f;      // regulation
        const float SandDepth = 0.30f;     // how far the sand sits below the deck
        const float EndMargin = 1.60f;     // clearance from the interior edge to the top of the runway
        const float SlabThickness = 0.5f;  // same as the track deck

        public static PitInfo Build(Transform parent, KartTrackBuilder.TrackInfo track,
                                    Material infield, Material runway, Material sand, Material line,
                                    Material foul, Material column, float pitLength = 8f, float lateral = -2.4f)
        {
            float halfLen = track.halfLength;
            float ri = track.radius - track.width * 0.5f;       // inner edge of the deck ribbon
            float top = track.deckTopY;
            float slabBottom = top - SlabThickness;
            float slabY = top - SlabThickness * 0.5f;
            float halfPit = PitWidth * 0.5f;
            float halfRun = RunwayWidth * 0.5f;

            // Solve the event into the interior, keeping at least an 8 m run-up.
            float interiorHalfX = halfLen + ri;
            float runwayStartX = -interiorHalfX + EndMargin;
            float pitFarX = halfLen;                            // the sand stops where the east cap starts
            pitLength = Mathf.Clamp(pitLength, 4f, (pitFarX - runwayStartX) - TakeoffGap - 8f);
            float pitNearX = pitFarX - pitLength;
            float takeoffX = pitNearX - TakeoffGap;
            float boardNearX = takeoffX - BoardDepth;
            float runwayLength = takeoffX - runwayStartX;
            float sandY = top - SandDepth;

            var root = new GameObject("LongJump").transform;
            root.SetParent(parent != null ? parent : track.root, false);
            root.localPosition = Vector3.zero;
            root.localRotation = Quaternion.identity;
            // The deck fills the interior in the loop's frame; the event itself sits on its own offset
            // node, so everything from the runway to the tick marks is authored on z = 0 and shifted once.
            var deck = new GameObject("InfieldDeck").transform; deck.SetParent(root, false);
            var legs = new GameObject("Legs").transform; legs.SetParent(root, false);
            var ev = new GameObject("Event").transform; ev.SetParent(root, false);
            ev.localPosition = new Vector3(0f, 0f, lateral);
            var pit = new GameObject("Pit").transform; pit.SetParent(ev, false);
            var lane = new GameObject("Runway").transform; lane.SetParent(ev, false);
            var marks = new GameObject("Markings").transform; marks.SetParent(ev, false);

            Debug.Log($"[LongJump] interior {2f * interiorHalfX:F1} x {2f * ri:F1} m | " +
                      $"runway {runwayLength:F1} m, board at x {takeoffX:F2}, pit {pitLength:F1} x {PitWidth:F2} m, " +
                      $"sand {SandDepth * 100f:F0} cm below the deck, centre line {lateral:+0.0;-0.0} m off the loop's");

            // ---- infield floor, cut around the pit -------------------------------------------------
            // Everything west of the sand, full interior width.
            KartTrackBuilder.Box(deck, "Infield_Main",
                new Vector3((pitNearX - halfLen) * 0.5f, slabY, 0f),
                new Vector3(pitNearX + halfLen, SlabThickness, 2f * ri), Quaternion.identity, infield);

            // Beside the sand, north and south: the slot is at z = lateral, so the two strips differ in width.
            foreach (float sz in new[] { 1f, -1f })
            {
                float edge = lateral + sz * halfPit;              // the sand's edge on this side
                float rim = sz * ri;                              // the interior's edge on this side
                KartTrackBuilder.Box(deck, sz > 0 ? "Infield_Beside_N" : "Infield_Beside_S",
                    new Vector3((pitNearX + pitFarX) * 0.5f, slabY, (edge + rim) * 0.5f),
                    new Vector3(pitLength, SlabThickness, Mathf.Abs(rim - edge)), Quaternion.identity, infield);
            }

            // Rounded ends: filled half discs, built as annular sectors with a hairline inner radius so
            // the same wedge helper that makes the track curves can make a solid cap.
            foreach (float sx in new[] { 1f, -1f })
            {
                Vector3 c = new Vector3(sx * halfLen, 0f, 0f);
                float dTheta = Mathf.PI / KartTrackBuilder.CurveSegments;
                string tag = sx > 0 ? "E" : "W";
                for (int i = 0; i < KartTrackBuilder.CurveSegments; i++)
                {
                    float t0 = Mathf.PI * 0.5f - i * dTheta;
                    KartTrackBuilder.Wedge(deck, $"Infield_Cap_{tag}_{i}", c, sx, 0.02f, ri, t0, t0 - dTheta,
                        slabBottom, top, infield);
                }
            }

            // ---- the sand, recessed into the slot the floor left -----------------------------------
            KartTrackBuilder.Box(pit, "Sand",
                new Vector3((pitNearX + pitFarX) * 0.5f, (slabBottom + sandY) * 0.5f, 0f),
                new Vector3(pitLength, sandY - slabBottom, PitWidth), Quaternion.identity, sand);

            // White rim so the edge of the sand reads from the broadcast cameras.
            foreach (float sz in new[] { 1f, -1f })
                KartTrackBuilder.Box(marks, sz > 0 ? "PitRim_N" : "PitRim_S",
                    new Vector3((pitNearX + pitFarX) * 0.5f, top + 0.01f, sz * (halfPit + 0.05f)),
                    new Vector3(pitLength + 0.2f, 0.02f, 0.1f), Quaternion.identity, line);
            KartTrackBuilder.Box(marks, "PitRim_Far",
                new Vector3(pitFarX + 0.05f, top + 0.01f, 0f),
                new Vector3(0.1f, 0.02f, PitWidth + 0.2f), Quaternion.identity, line);

            // ---- runway, board and foul line -------------------------------------------------------
            KartTrackBuilder.Box(lane, "RunwaySurface",
                new Vector3((runwayStartX + takeoffX) * 0.5f, top + 0.01f, 0f),
                new Vector3(runwayLength, 0.04f, RunwayWidth), Quaternion.identity, runway);
            foreach (float sz in new[] { 1f, -1f })
                KartTrackBuilder.Box(marks, sz > 0 ? "RunwayEdge_N" : "RunwayEdge_S",
                    new Vector3((runwayStartX + takeoffX) * 0.5f, top + 0.032f, sz * (halfRun - 0.03f)),
                    new Vector3(runwayLength, 0.016f, 0.05f), Quaternion.identity, line);

            KartTrackBuilder.Box(lane, "TakeoffBoard",
                new Vector3((boardNearX + takeoffX) * 0.5f, top + 0.02f, 0f),
                new Vector3(BoardDepth, 0.05f, RunwayWidth), Quaternion.identity, line);
            // The plasticine indicator strip: the far edge of it is the foul line.
            KartTrackBuilder.Box(lane, "FoulIndicator",
                new Vector3(takeoffX + 0.05f, top + 0.02f, 0f),
                new Vector3(0.1f, 0.05f, RunwayWidth), Quaternion.identity, foul);

            // ---- distance ticks along the north edge of the pit, measured from the foul line --------
            float reach = pitFarX - takeoffX;
            for (int m = 3; m <= Mathf.FloorToInt(reach); m++)
            {
                bool five = m % 5 == 0;
                KartTrackBuilder.Box(marks, $"Mark_{m}m",
                    new Vector3(takeoffX + m, top + 0.01f, halfPit + 0.35f),
                    new Vector3(0.07f, 0.02f, five ? 0.9f : 0.45f), Quaternion.identity, line);
            }

            // ---- legs down onto whatever roof is below ---------------------------------------------
            Physics.SyncTransforms();
            for (float x = -interiorHalfX + 1f; x <= interiorHalfX - 1f + 0.01f; x += 5f)
                for (float z = -ri + 1f; z <= ri - 1f + 0.01f; z += 5f)
                {
                    if (!InsideInterior(x, z, halfLen, ri - 0.8f)) continue;
                    KartTrackBuilder.Leg(legs, track.root, new Vector3(x, slabBottom, z), column);
                }

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = true;
            EditorUtility.SetDirty(root.gameObject);

            return new PitInfo
            {
                root = ev, lateral = lateral,
                runwayStartX = runwayStartX, takeoffX = takeoffX, boardDepth = BoardDepth,
                pitNearX = pitNearX, pitFarX = pitFarX,
                runwayWidth = RunwayWidth, pitWidth = PitWidth,
                surfaceY = top, sandY = sandY,
            };
        }

        /// <summary>Stadium test: the rectangle between the end centres, plus a disc at each end.</summary>
        static bool InsideInterior(float x, float z, float halfLen, float r)
        {
            if (Mathf.Abs(x) <= halfLen) return Mathf.Abs(z) <= r;
            float dx = Mathf.Abs(x) - halfLen;
            return dx * dx + z * z <= r * r;
        }
    }
}
