using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace HelmetInspection
{
    public sealed class RecenterOnSecondaryButton : MonoBehaviour
    {
        [SerializeField] XRNode hand = XRNode.RightHand;
        bool m_WasPressed;

        void Update()
        {
            var device = InputDevices.GetDeviceAtXRNode(hand);
            var pressed = device.TryGetFeatureValue(CommonUsages.secondaryButton, out var value) && value;
            if (pressed && !m_WasPressed)
            {
                var displays = new List<XRInputSubsystem>();
                SubsystemManager.GetSubsystems(displays);
                foreach (var subsystem in displays)
                    if (subsystem.running)
                        subsystem.TryRecenter();
            }
            m_WasPressed = pressed;
        }
    }
}
