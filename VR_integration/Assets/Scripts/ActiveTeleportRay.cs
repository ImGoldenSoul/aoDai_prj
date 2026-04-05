using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.InputSystem;

public class ActiveTeleportRay : MonoBehaviour
{
    public GameObject leftTeleportRay;
    public GameObject rightTeleportRay;
    public InputActionProperty leftTeleportActivate;
    public InputActionProperty rightTeleportActivate;

    void Update()
    {
        leftTeleportRay.SetActive(leftTeleportActivate.action.ReadValue<float>() > 0.1f);
        rightTeleportRay.SetActive(rightTeleportActivate.action.ReadValue<float>() > 0.1f);
    }
}
