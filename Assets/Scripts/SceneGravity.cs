using UnityEngine;

public class SceneGravity : MonoBehaviour
{
    [SerializeField] private Vector3 gravity = new Vector3(0f, -9.81f, 0f);

    private void Awake()
    {
        Physics.gravity = gravity;
    }

    private void OnValidate()
    {
        if (Application.isPlaying)
            Physics.gravity = gravity;
    }
}
