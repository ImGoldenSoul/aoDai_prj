using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class RecenterOrigin : MonoBehaviour
{
    public Transform head;
    public Transform origin;
    public Transform target;
    public InputActionProperty recenterButton;
    // Start is called before the first frame update
    public void Recenter()
    {
        Vector3 offset_position = head.position - origin.position;
        Vector3 offset_rotation = head.rotation.eulerAngles - origin.rotation.eulerAngles;
        offset_position.y = 0; // Keep the vertical position unchanged
        offset_rotation.x = 0; // Keep the pitch unchanged
        offset_rotation.z = 0; // Keep the roll unchanged
        origin.position = target.position - offset_position;
        origin.rotation = Quaternion.Euler(target.rotation.eulerAngles - offset_rotation);
    }
 
    // Update is called once per frame
    void Update()
    {
        if(recenterButton.action.WasPressedThisFrame())
        {
            Recenter();
        }
    }
}
