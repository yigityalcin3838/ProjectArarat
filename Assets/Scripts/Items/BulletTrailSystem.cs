using System.Collections.Generic;
using UnityEngine;

// The streak a round leaves behind it. Put one in the scene and point it at a trail
// prefab, the way SurfaceSystem is set up -- anything that fires reports the line its
// shot travelled and this decides what that looks like.
//
// ── THE TRAIL IS NOT THE BULLET, AND THAT SEPARATION IS THE WHOLE DESIGN ─────────────
//
// The round is a raycast in Weapon.FireHitscan. It is instantaneous and it is the only
// thing that decides anything: where the shot landed, what it hit, what that does. By
// the time this class hears about a shot, all of that has already happened.
//
// What arrives here is two points and nothing else. This has no collider, casts nothing,
// reads no hit and cannot reach the thing that was hit. A trail that lands late, starts
// in the wrong place, or is switched off entirely changes what the player sees and
// nothing else. That is the property worth protecting:
// the moment the visual becomes the projectile, its travel time becomes latency on the
// hit, its collider becomes a second opinion about what was in the way, and the pool
// running dry becomes a shot that does no damage.
//
// It also means the trail is free to lie for the sake of reading well. It flies at a
// speed chosen to be SEEN rather than the speed of a bullet -- a real round crosses a
// room inside a frame, so an honest one would be invisible. Nothing downstream cares.
//
// ── WHY A TRAIL RENDERER AND A POOL ──────────────────────────────────────────────────
//
// Unity's own TrailRenderer already does the part that is fiddly: laying a ribbon behind
// a moving point, tapering it, fading it over a lifetime. It wants a transform that
// actually moves across frames, so this moves one, and the look is authored on the
// prefab in the Inspector rather than in numbers here.
//
// Pooled because a rifle produces one of these per round per pellet, and Instantiate and
// Destroy at that rate is the kind of garbage that shows up as a stutter during sustained
// fire rather than as a frame cost anyone can point at.
[DisallowMultipleComponent]
public class BulletTrailSystem : MonoBehaviour
{
    // Authored in the Editor: the TrailRenderer's material and the SHAPE of its width
    // curve and colour, plus -- on it or under it -- a ParticleSystem for the round itself
    // and a Light if it should throw one. How it looks lives there; how big it is and how
    // it moves live here.
    //
    // The particle is not decoration on the streak, it is the half of it that works when
    // the streak cannot. See Trail.headEffect.
    //
    // Its Time and its width multiplier are the exceptions, overwritten on every spawn,
    // so whatever they are set to on the prefab does not matter -- see length and width
    // for why those two had to come back out of it. A Light on it is not an exception:
    // every figure on it is its own.
    [SerializeField] private TrailRenderer trailPrefab;

    // Metres per second, held the whole way. Deliberately not a real muzzle velocity: a
    // rifle round does 800 and would cross any room the player is standing in inside a
    // single frame, so the streak would either not be drawn at all or be drawn once, in
    // full, which is a line rather than a shot. Fast enough to read as supersonic, slow
    // enough to be seen.
    //
    // It was briefly a starting speed, eased off along the flight by an arrival fraction,
    // to make a distant round look like it hung in the air. That is gone: a round crossing
    // at one speed is the plain reading, and the reason it is worth writing down is that
    // the slowing dragged a parabola and a flight-time correction in behind it, both of
    // which exist only to keep an exact arrival possible while decelerating. At one speed
    // the arrival is just distance over speed and there is nothing to correct.
    [SerializeField] private float speed = 220f;

    // How far along the line the streak actually starts, in metres.
    //
    // This exists because of the lens. The muzzle is on the view model layer and is drawn
    // by ViewModelLensFeature at its own narrower field of view; the trail is an ordinary
    // world object at the world's. Two projections means the barrel's tip and the world
    // point it occupies are not the same pixel, so a streak begun exactly at the muzzle
    // starts visibly beside the barrel rather than out of it. Pushing the start a little
    // way down the line puts it past the gap, where both projections agree closely enough
    // that nothing reads as misaligned.
    //
    // It costs nothing in truth: the round's own line is unchanged, and the first few
    // centimetres of a trail are inside the muzzle flash anyway.
    [SerializeField] private float startOffset = 0.35f;

    // How long the streak is, in metres -- the distance from its head to where it has
    // faded to nothing.
    //
    // THE LENGTH IS AUTHORED AND THE LIFETIME IS DERIVED, not the other way round, and
    // they are the same fact so only one of them can be a field. A TrailRenderer fades
    // over a time, and the ribbon that time produces is however far the head travelled
    // during it: length = speed x lifetime. Two of those three as separate fields is one
    // number written twice, and the copy that drifts is whichever one nobody is looking
    // at.
    //
    // Length is the one worth keeping because it is the one that is visible. "A thirty
    // metre streak" is what is on the screen and can be judged against the room it
    // crosses; "a fade of 0.13 seconds" is that same thing requiring a multiplication
    // first. It also behaves better when speed changes: a faster tracer stays the same
    // length instead of growing, which is what anyone changing speed meant.
    [SerializeField] private float length = 30f;

    // How thick the streak is, in metres.
    //
    // Scales the prefab's width curve rather than replacing it, which is the same split
    // as everything else here: the SHAPE of the taper is a look and belongs in the
    // Inspector, the size it is drawn at is a number and belongs where it can be read
    // against the others. A curve running 1 to 0 tapers to a point at whatever thickness
    // this sets.
    [SerializeField] private float width = 0.03f;

    // Seconds a streak that HIT something stays at the impact before it goes out.
    //
    // It stays where it hit and stays whole -- the head stops on the point, the ribbon
    // behind it stops expiring, and what was on the screen at the instant of impact is
    // held there and then gone. Not a fade and not a drift: the round arrived somewhere
    // and that somewhere is where the streak is left.
    //
    // Holding it takes one thing beyond waiting, and it is the reason this is not simply
    // a timer. A TrailRenderer drops points once they are older than its time, and that
    // time is Length -- so left alone the ribbon eats itself from the tail within a
    // fraction of a second of the head stopping, and a longer hold would be holding
    // nothing. On arrival the time is opened up by exactly this delay, which stops the
    // points already laid down from expiring without touching how long the streak was
    // while it flew.
    [SerializeField] private float hitDeathDelay = 0.25f;

    // Seconds a streak that hit NOTHING flies for before it goes out.
    //
    // Only a miss has this question. A round that hit something ends where it hit: the
    // impact is there, the surface is there, and the streak stopping dead against it is
    // the whole reading of a bullet arriving.
    //
    // A miss has no such place, and IT MUST NOT BORROW THE WEAPON'S. The endpoint the
    // weapon reports for a miss is the far end of its Max Range, which is a figure about
    // how far a shot is allowed to do damage -- an arbitrary point in mid-air as far as
    // anything visible is concerned, and one that would have a tracer wink out at exactly
    // a hundred metres because that is where the gameplay trace happened to stop. Raising
    // Max Range for the look would quietly change what the weapon can kill.
    //
    // So a miss gets a duration of its own and that is all it gets: it flies for this
    // long, covering speed x this, and goes out. Where the weapon stopped caring has
    // nothing to do with it.
    [SerializeField] private float missFlightTime = 0.5f;

    // Seconds of fade, worked out from length and speed.
    //
    // It does two jobs and that is why it is derived rather than stored: it is what the
    // renderer fades over, and it is how long the object stays busy before it can be
    // reused. Those have to be the same number, or a streak is either recycled while
    // still on screen or held long after it has gone. One expression, read by both.
    //
    // A speed of nothing would divide to infinity and hold every trail forever, so it is
    // floored -- a streak that does not move is a setup mistake, and it should look like
    // a dot rather than take the pool with it.
    private float Lifetime => length / Mathf.Max(speed, 0.01f);

    public static BulletTrailSystem Instance { get; private set; }

    // One in flight. The renderer and its transform are cached because this is touched
    // every frame for every live streak, and a transform lookup per entry per frame is
    // the cost this whole class exists to avoid.
    private class Trail
    {
        public TrailRenderer renderer;
        public Transform transform;

        // The round itself, as opposed to the streak it leaves. Null on a prefab that
        // carries no ParticleSystem.
        //
        // A RIBBON CANNOT SHOW A ROUND COMING AT YOU OR GOING AWAY FROM YOU, and no
        // setting on it can. A TrailRenderer is a strip of quads along the path, and View
        // alignment turns that strip to face the camera -- which fixes which way it is
        // facing and does nothing about which way it is POINTING. A shot fired at
        // something far away travels almost straight down the view axis, so the path it
        // is drawn along projects to nearly a point: the whole streak collapses into its
        // own width and there is nothing there to read. Widening it only makes the dot
        // bigger, which was the previous answer and is why it was never quite the answer.
        //
        // A particle is not a strip. It is a billboard at a point, so it has no direction
        // to be foreshortened along and looks the same whichever way the round is going.
        // Coming at you it is the round; crossing you it is the round with the streak
        // behind it. That is the pair, and each is for the case the other cannot cover.
        //
        // Unity's own, rather than a quad turned to face the camera here. It already
        // billboards, already scales over a lifetime, already takes an additive material
        // into bloom, and it rides this transform because it is under it -- so the whole
        // look is authored in the Inspector and none of it is described twice in code.
        public ParticleSystem headEffect;

        // What the round throws onto what it passes. Null on a prefab that carries no
        // Light, and every use of it is guarded rather than the whole thing being gated
        // by a field somewhere -- there is nothing to gate, the absence IS the setting,
        // and a second switch saying the same thing is one that can disagree with the
        // component it is about.
        //
        // NOTHING HERE WRITES TO IT. Colour, intensity, range and shadows are all the
        // component's, read by Unity and never by this class, which only ever switches it
        // on and off with the streak.
        //
        // That is a different answer from the one the ribbon's time and width got, and
        // the difference is not taste. Those two had to come out of the prefab because
        // code DERIVES from them: the time doubles as how long a pooled object stays
        // busy, and the width is scaled by the shot's range. Nothing derives from the
        // light, so nothing here needs to know what it is set to.
        //
        // It rides the head rather than the ribbon, which is the only place it can be --
        // a light is a point and the streak is a line, so it goes where the round is and
        // the ribbon is what that leaves behind. Passing a wall, the bright spot travels
        // along it and the streak follows, which is what makes a tracer read as something
        // moving rather than as a line being drawn.
        public Light lightSource;
        public Vector3 from;
        public Vector3 to;
        public float distance;
        public float travelled;

        // Seconds to stay put once the head has arrived. It carries the whole of the
        // difference between a hit and a miss, which is why there is no bool for that
        // here: a hit is given the delay, a miss is given nothing, and the phase after
        // arrival then reads the same for both.
        public float holdRemaining;
        public bool hasArrived;
    }

    private readonly List<Trail> _pool = new List<Trail>();
    private readonly List<Trail> _active = new List<Trail>();

    private void OnEnable()
    {
        // Last one to enable wins, matching SurfaceSystem: an additively loaded scene
        // overriding the look is the useful behaviour.
        Instance = this;
    }

    private void OnDisable()
    {
        if (Instance == this)
            Instance = null;
    }

    // Reported by whatever fired: where the round left, where its trace ended, and
    // whether that end is something it actually hit.
    //
    // ON A MISS THE FAR POINT IS A DIRECTION, NOT A DESTINATION. A miss still has to be
    // reported -- a streak that only appears when something was hit turns every miss into
    // a weapon that did not fire -- but the point it comes with is wherever the weapon's
    // Max Range stopped, which is a gameplay figure with no business setting how far a
    // tracer is seen to fly. So the bool decides which half of it to believe: on a hit,
    // the point; on a miss, only the line it points down.
    //
    // A plain bool rather than the RaycastHit for the same reason everything else here is
    // a plain anything: this must not be able to reach what was hit.
    public void Report(Vector3 from, Vector3 to, bool hitSomething)
    {
        if (trailPrefab == null)
            return;

        Vector3 direction = to - from;

        // NOT named length. The field by that name is the streak's, this is the shot's,
        // and a local shadowing a field is how one gets read where the other was meant.
        float shotDistance = direction.magnitude;

        if (shotDistance <= 0.0001f)
            return;

        direction /= shotDistance;

        // Clamped rather than skipped, so a shot into a wall at arm's length still gets
        // its streak -- just a very short one. Skipped, point-blank fire would be the one
        // range where the weapon looks dead.
        Vector3 start = from + direction * Mathf.Min(startOffset, shotDistance);

        // WHERE IT ENDS IS ONE QUESTION WITH TWO ANSWERS, and settling it here -- along
        // with the hold below -- is what leaves Update with no idea whether it is moving
        // a hit or a miss. It flies to a point, waits out whatever hold it was given, and
        // goes out.
        //
        // A hit ends at the point it hit, reported. A miss has no point of its own and is
        // given one from its own duration instead -- the reported endpoint is used only
        // for the direction it implies, never the distance, so the weapon's Max Range
        // cannot decide how far a streak is seen to travel.
        //
        // How far that duration reaches is simply how far it gets: one speed, so speed
        // times seconds.
        Vector3 end = hitSomething
            ? to
            : start + direction * (speed * missFlightTime);

        Trail trail = Take();

        trail.from = start;
        trail.to = end;
        trail.distance = Vector3.Distance(start, end);
        trail.travelled = 0f;
        trail.hasArrived = false;

        // Only a hit has somewhere worth being left. A miss arrives at a point in mid-air
        // that nothing marks, so there is nothing there to stay at and it simply ends.
        trail.holdRemaining = hitSomething ? hitDeathDelay : 0f;

        // MOVED, THEN CLEARED, THEN ALLOWED TO EMIT, and all three while the object has
        // been active the whole time. Clearing after the move is what stops the first
        // frame drawing a line from wherever the last shot ended to where this one
        // starts -- a streak across the level, rooftop to doorway. Clearing before it
        // would leave the same problem one frame later.
        trail.transform.position = start;
        trail.renderer.Clear();
        trail.renderer.emitting = true;

        // Pushed in per spawn rather than once at instantiate, so the fields above can be
        // dragged in play mode and the next shot shows the result. It is the same write
        // either way; a trail is not spawned often enough for it to be worth saving.
        trail.renderer.time = Lifetime;
        trail.renderer.widthMultiplier = width;

        // Cleared before it is played, for the reason the ribbon is: a pooled system still
        // holds the particles it had when it was put away, and a reused one would start
        // by dropping the last shot's round wherever this one begins.
        if (trail.headEffect != null)
        {
            trail.headEffect.Clear(withChildren: true);
            trail.headEffect.Play(withChildren: true);
        }

        // Switched on and nothing else. Every figure the light has -- colour, intensity,
        // range, the lot -- is the component's own, untouched.
        if (trail.lightSource != null)
            trail.lightSource.enabled = true;

        _active.Add(trail);
    }

    private void Update()
    {
        // Backwards, because finished streaks are removed as they are found and a
        // forward loop would skip the entry that slid into the freed index.
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            Trail trail = _active[i];

            // A pooled object can be destroyed out from under the pool -- a scene
            // unloading, or Autodestruct left ticked on the prefab, which has the
            // TrailRenderer delete its own GameObject the moment a trail finishes. Both
            // leave an entry whose renderer is gone, and without this the pool throws
            // once per frame forever afterwards. Dropped rather than replaced: something
            // purely visual going quiet is the right answer to losing its object.
            if (trail.renderer == null)
            {
                _active.RemoveAt(i);
                continue;
            }

            if (!trail.hasArrived)
            {
                trail.travelled += speed * Time.deltaTime;

                if (trail.travelled < trail.distance)
                {
                    trail.transform.position = Vector3.Lerp(
                        trail.from, trail.to, trail.travelled / trail.distance);

                    continue;
                }

                // Landed exactly on the endpoint rather than wherever the last step
                // overshot to. A hit's impact effect is already at that point, and a
                // streak that stops a few centimetres past it reads as the round going
                // through what it hit.
                trail.transform.position = trail.to;
                trail.hasArrived = true;

                // Nothing more is laid down -- the head is not going anywhere. The ribbon
                // already behind it is the whole of what is left to show.
                trail.renderer.emitting = false;

                // The round stops being emitted for the same reason, and what is already
                // out is left to finish rather than cut: a stationary head would otherwise
                // pile particles onto one point for the whole hold.
                if (trail.headEffect != null)
                    trail.headEffect.Stop(true, ParticleSystemStopBehavior.StopEmitting);

                // AND IT IS KEPT, by opening the renderer's window rather than by asking
                // it to wait. Points are dropped once they are older than time, and time
                // is the streak's length in seconds, so a ribbon left to itself is eaten
                // from the tail almost as soon as the head stops -- the hold would be
                // holding an empty object. Extended by exactly the hold, the oldest point
                // still alive at this instant survives to the end of it, and nothing about
                // how long the streak was in flight has been touched.
                //
                // Set even when the hold is zero, which costs a write and keeps the two
                // paths from needing to know about each other.
                trail.renderer.time = Lifetime + trail.holdRemaining;
            }

            trail.holdRemaining -= Time.deltaTime;

            if (trail.holdRemaining > 0f)
                continue;

            Release(trail);
            _active.RemoveAt(i);
        }
    }

    // Out and parked, in the state Take expects to find: not emitting, no points, dark.
    private void Release(Trail trail)
    {
        trail.renderer.emitting = false;

        // Cleared as well as stopped here, unlike on arrival: this is the end, and a
        // parked trail holding live particles would go on showing a round at whatever
        // point it was parked at.
        if (trail.headEffect != null)
            trail.headEffect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        // Off with the streak, not a moment after it. The light is the round and the
        // round has stopped existing; left on, a parked trail would go on lighting
        // whatever it was parked next to.
        if (trail.lightSource != null)
            trail.lightSource.enabled = false;

        // Emptied on the way back to the pool as well as on the way out of it, which is
        // not belt and braces for its own sake: it means a parked trail holds no points
        // at all, so even if the clear on reuse were ever to miss, there is nothing of
        // the old shot left for it to draw.
        trail.renderer.Clear();

        _pool.Add(trail);
    }

    // Grown on demand and never capped, so it settles at the most that were ever in
    // flight at once and stays there.
    //
    // NO CEILING, and that is deliberate after a ceiling went wrong here. The size this
    // reaches is flight time plus the lifetime, times the fastest anything fires -- a couple
    // of dozen for a rifle, and a TrailRenderer costs a transform and a handful of
    // vertices. It is not a number worth defending, and defending it is what caused the
    // problem: refusing a shot at the cap made trails stop appearing outright under
    // sustained fire, and a rendering setting silently switching the feature off is not a
    // failure anyone can read. Growing instead has no failure to read.
    private Trail Take()
    {
        int last = _pool.Count - 1;

        if (last >= 0)
        {
            Trail pooled = _pool[last];
            _pool.RemoveAt(last);

            // An object can be destroyed out from under the pool -- a scene unloading,
            // or Autodestruct ticked on the prefab -- so a parked entry is checked before
            // it is handed back out rather than after it has been used.
            if (pooled.renderer != null)
                return pooled;
        }

        // Parented HERE and not to the weapon or the muzzle, which is not an oversight: a
        // streak is in the world from the moment it leaves the barrel, and carried by the
        // weapon it would swing with every turn of the view -- a laser pointer rather than
        // a round in flight. This object is only somewhere to keep them in the hierarchy,
        // so it belongs on something that does not move; the positions written below are
        // world space and a moving parent would drag every live streak with it.
        TrailRenderer renderer = Instantiate(trailPrefab, transform);

        // LEFT ACTIVE, FOR ITS WHOLE LIFE, and this is the one thing about pooling a
        // TrailRenderer that has to be got right.
        //
        // The obvious way to park one is to deactivate the object and switch it back on
        // when it is needed, and it does not survive that. A TrailRenderer's points carry
        // timestamps and its geometry is rebuilt by the trail system, which the object
        // leaves and rejoins across the toggle; Clear() called on the frame it comes back
        // is operating on state that has not been rebuilt yet, so it does not take, and
        // what happens next depends on internals nobody should be depending on. The
        // symptom is the first few shots drawing and everything after them drawing
        // nothing, which reads as the system dying rather than as a lifecycle problem.
        //
        // An always-active renderer holding no points draws nothing and costs nothing, so
        // there is no reason to toggle it in the first place. Not emitting and empty IS
        // the parked state.
        renderer.emitting = false;
        renderer.Clear();

        // Looked up once, here, rather than every spawn. Whether the prefab has a Light is
        // a fact about the prefab and cannot change between shots, and a
        // GetComponentInChildren per pellet per round is exactly the per-shot work this
        // pool exists to avoid.
        Light lightSource = renderer.GetComponentInChildren<Light>(includeInactive: true);

        if (lightSource != null)
            lightSource.enabled = false;

        // Same once-only lookup, same reason. Stopped rather than left to its own Play On
        // Awake, so a freshly made trail is parked exactly like a recycled one.
        ParticleSystem headEffect = renderer.GetComponentInChildren<ParticleSystem>(includeInactive: true);

        if (headEffect != null)
            headEffect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        return new Trail
        {
            renderer = renderer,
            transform = renderer.transform,
            headEffect = headEffect,
            lightSource = lightSource,
        };
    }
}
