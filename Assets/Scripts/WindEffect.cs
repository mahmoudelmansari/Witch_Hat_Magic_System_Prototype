using UnityEngine;

public class WindEffect : MonoBehaviour
{
    public Vector3 windDirection;
    public float force = 5f;
    public float lifetime = 2f;

    void Start()
    {
        //Destroy(gameObject, lifetime);
    }

    void OnTriggerStay(Collider other)
    {
        var rb = other.GetComponent<Rigidbody>();
        if (rb != null)
            rb.AddForce(rb.transform.right * force);
    }
}