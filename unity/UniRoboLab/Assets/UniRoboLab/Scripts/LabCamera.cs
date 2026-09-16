using UnityEngine;

/// <summary>
/// 右ドラッグで回転、ホイールでズーム、中ドラッグで平行移動する簡単な注視カメラ。
/// settings.camera (position / look_at、ROS 座標) があれば起動時にそれに合わせる。
/// </summary>
public class LabCamera : MonoBehaviour
{
    public Vector3 target = Vector3.zero;
    public float distance = 3f;
    float m_Yaw = 45f, m_Pitch = 25f;

    void Start()
    {
        CameraViewConfig view = SimulationResources.Settings?.camera;
        if (view != null && view.HasPosition)
        {
            Vector3 pos = CameraViewConfig.RosToUnity(view.position);
            if (view.HasLookAt) target = CameraViewConfig.RosToUnity(view.look_at);
            Vector3 d = pos - target;
            distance = Mathf.Max(0.2f, d.magnitude);
            m_Yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            m_Pitch = Mathf.Asin(Mathf.Clamp(d.y / distance, -1f, 1f)) * Mathf.Rad2Deg;
        }
        Apply();
    }

    void Update()
    {
        if (Input.GetMouseButton(1))
        {
            m_Yaw += Input.GetAxis("Mouse X") * 3f;
            m_Pitch = Mathf.Clamp(m_Pitch - Input.GetAxis("Mouse Y") * 3f, -89f, 89f);
        }
        if (Input.GetMouseButton(2))
        {
            target -= transform.right * Input.GetAxis("Mouse X") * distance * 0.02f;
            target -= transform.up * Input.GetAxis("Mouse Y") * distance * 0.02f;
        }
        float wheel = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(wheel) > 1e-4f) distance = Mathf.Clamp(distance * (1f - wheel), 0.2f, 100f);
        Apply();
    }

    void Apply()
    {
        Quaternion rot = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
        transform.position = target + rot * new Vector3(0f, 0f, -distance);
        transform.rotation = rot;
    }
}
