using UnityEngine;

public class Ladder : MonoBehaviour
{
    [SerializeField] private Transform botStart;
    [SerializeField] private Transform topStart;
    [SerializeField] private Transform tipPoint;

    public Vector3 BotStart => botStart.position;
    public Vector3 TopStart => topStart.position;
    public Vector3 TipPoint => tipPoint.position;
    public Vector3 Forward => transform.forward;
}
