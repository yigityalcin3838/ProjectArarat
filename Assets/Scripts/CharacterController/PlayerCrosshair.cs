using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Draws the reticle on PlayerLook.InteractionRay -- where the player is pointing.
///
/// This belongs to the character rather than to whatever is in its hands. It is up
/// with empty hands, through every item swap, and while carrying something that has
/// nothing to do with shooting, because the thing it marks is not "where the gun
/// points" but "what I am addressing" -- the door about to be opened, the car about
/// to be entered, the wall about to be shot.
///
/// It draws the same ray the round leaves along and the same ray interaction reaches
/// through, so the reticle cannot end up marking a different spot from the one that
/// actually gets hit. That is not a thing to keep in sync; it is one ray.
///
/// The exception is aiming: down a sight the sight is the aim, and a floating reticle
/// beside it just fights the iron or the optic for attention.
/// </summary>
public class PlayerCrosshair : MonoBehaviour
{
    [Header("Reticle")]
    // Drop the scene's RawImage straight in here -- that is the whole setup. A
    // RawImage rather than an Image so any texture in the project works as-is, with
    // no sprite import settings to get right. Anchor and pivot centred (0.5, 0.5):
    // the position written below is measured from there.
    [SerializeField] private RawImage crosshair;

    [Header("References")]
    [SerializeField] private PlayerLook playerLook;

    // Only read for IsAiming. Left empty the reticle simply never hides.
    [SerializeField] private PlayerMovement movement;

    [Header("Behaviour")]
    [SerializeField] private bool hideWhileAiming = true;

    // How far to look for something to sit on.
    [SerializeField] private float maxDistance = 200f;

    // Where the reticle sits when the ray hits nothing -- open sky, a long hallway.
    // Far enough out that it sits close to the eye line, since at distance the offset
    // between the eye and anything else stops mattering.
    [SerializeField] private float fallbackDistance = 60f;

    // Distance is smoothed, screen position is not.
    //
    // Where the reticle lands on screen depends on how far away the thing in front of
    // it is, so sweeping past the edge of a wall snaps the depth from 2 m to 60 m and
    // the reticle jumps. Easing the distance rather than the screen point keeps the
    // reticle exactly on the traced point whenever the depth is settled, and only
    // lags across the edge, where there is no correct answer anyway. 0 disables it.
    [SerializeField] private float distanceSmoothSpeed = 12f;

    // Below this the trace is ignored: pressed against a wall the eye is nearly
    // inside it, and a hit at a few centimetres throws the projection wildly off.
    [SerializeField] private float minDistance = 0.5f;

    private float _currentDistance;
    private bool _hasDistance;

    /// <summary>
    /// What the reticle is currently sitting on, in world space. Whatever the player
    /// is addressing -- useful to anything that wants the point rather than the ray.
    /// </summary>
    public Vector3 AimPoint { get; private set; }

    private void Awake() => SetVisible(false);

    // LateUpdate because the free aim the ray is built from is written in Update, as
    // is everything that moves the camera the projection is taken through. Script
    // execution order between those and this is not fixed; LateUpdate is after all of
    // them by definition, so the reticle is placed against the pose about to be
    // rendered rather than the previous frame's.
    private void LateUpdate()
    {
        if (crosshair == null || playerLook == null)
            return;

        if (hideWhileAiming && movement != null && movement.IsAiming)
        {
            Hide();
            return;
        }

        Camera camera = playerLook.RenderCamera;
        RectTransform reticle = crosshair.rectTransform;
        RectTransform parent = reticle.parent as RectTransform;

        if (camera == null || parent == null)
        {
            Hide();
            return;
        }

        Ray ray = playerLook.InteractionRay;

        // The same exclusions a round uses, from the one place the project agrees on
        // them -- otherwise the reticle would stop short on the movement capsule, or
        // on debris that a bullet passes straight through.
        float targetDistance = fallbackDistance;
        if (Physics.Raycast(ray, out RaycastHit hit, maxDistance,
                GameLayers.Queryable, QueryTriggerInteraction.Ignore)
            && hit.distance >= minDistance)
        {
            targetDistance = hit.distance;
        }

        if (!_hasDistance || distanceSmoothSpeed <= 0f)
        {
            _currentDistance = targetDistance;
            _hasDistance = true;
        }
        else
        {
            _currentDistance = Mathf.Lerp(_currentDistance, targetDistance,
                1f - Mathf.Exp(-distanceSmoothSpeed * Time.deltaTime));
        }

        AimPoint = ray.GetPoint(_currentDistance);

        Vector3 screenPoint = camera.WorldToScreenPoint(AimPoint);

        // Behind the eye. WorldToScreenPoint mirrors the result rather than failing,
        // so this has to be caught rather than left to produce a plausible wrong
        // answer.
        if (screenPoint.z <= 0f)
        {
            Hide();
            return;
        }

        // Overlay canvases take a null camera here; every other render mode takes the
        // canvas's own.
        Canvas canvas = crosshair.canvas;
        Camera uiCamera = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            ? canvas.worldCamera
            : null;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parent, screenPoint, uiCamera, out Vector2 localPoint))
        {
            Hide();
            return;
        }

        reticle.anchoredPosition = localPoint;
        SetVisible(true);
    }

    private void Hide()
    {
        SetVisible(false);

        // Coming back it should land on the current depth rather than easing in from
        // whatever was in front of the player when it went away.
        _hasDistance = false;
    }

    // enabled rather than SetActive, so the object stays alive and nothing has to be
    // re-found on the way back.
    private void SetVisible(bool visible)
    {
        if (crosshair != null && crosshair.enabled != visible)
            crosshair.enabled = visible;
    }
}
