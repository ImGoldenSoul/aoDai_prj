using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine.InputSystem;

/// <summary>
/// TaichiClothVR — nhận lưới vải từ Python Taichi server qua UDP và render lên Unity.
///
/// ── CỔNG UDP (phải khớp với cloth_taichi_server.py) ──────────────────────────
///   Python gửi mesh  → Unity nhận ở receivePort = 5007
///   Unity gửi grab   → Python nhận ở sendPort   = 5008
///   (cloth_taichi_server.py: PORT_SEND_TO_UNITY=5007, PORT_RECV_FROM_UNITY=5008)
/// ─────────────────────────────────────────────────────────────────────────────
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class TaichiClothVR : MonoBehaviour
{
    [Header("Network Settings")]
    [Tooltip("IP của Python server")]
    public string serverIP = "127.0.0.1";

    [Tooltip("Unity NHẬN mesh từ Python ở port này. Phải bằng PORT_SEND_TO_UNITY trong .py (mặc định 5007)")]
    public int receivePort = 5007;

    [Tooltip("Unity GỬI grab tới Python ở port này. Phải bằng PORT_RECV_FROM_UNITY trong .py (mặc định 5008)")]
    public int sendPort = 5008;

    [Header("Cloth Resolution — phải khớp NW, NH trong file .py")]
    public int width  = 30;
    public int height = 30;

    [Header("VR Interaction")]
    [Tooltip("Kéo Right Controller vào đây để làm nguồn bắn tia Laser")]
    public Transform vrController;

    [Tooltip("Quả cầu đỏ hiển thị điểm tương tác")]
    public Transform grabSphere;

    [Tooltip("Chọn XRI RightHand/Select Value hoặc Activate Value")]
    public InputActionReference triggerAction;

    [Header("Debug")]
    [Tooltip("Bật để log số gói tin nhận được mỗi giây vào Console")]
    public bool debugLog = true;

    // ── state ────────────────────────────────────────────────────────────────
    [SerializeField] private bool _isGrabbing = false;
    private bool _wasGrabbing = false;

    private Vector3 _grabOffset;
    private Vector3 _currentWorldTargetPos;
    private MeshCollider _meshCollider;

    private UdpClient  _receiver;
    private UdpClient  _sender;
    private IPEndPoint _receiveEndPoint;
    private IPEndPoint _sendEndPoint;

    private Mesh      _clothMesh;
    private Vector3[] _vertices;

    // Debug counters
    private int   _packetsReceived = 0;
    private float _debugTimer      = 0f;

    // Mesh override (dùng cho piece sau khi cắt — gọi bởi MeshCutter_Taichi)
    private bool _meshOverride = false;

    // =========================================================================
    //  PUBLIC: Gọi bởi MeshCutter_Taichi trước khi piece được kích hoạt
    // =========================================================================
    public void InitializeWithMesh(Mesh existingMesh)
    {
        if (existingMesh == null) return;
        _meshOverride = true;

        _clothMesh = new Mesh();
        _clothMesh.MarkDynamic();
        _clothMesh.SetVertices(existingMesh.vertices);
        _clothMesh.SetNormals(existingMesh.normals);
        _clothMesh.SetUVs(0, existingMesh.uv);
        _clothMesh.SetTriangles(existingMesh.triangles, 0);
        _clothMesh.RecalculateBounds();

        _vertices = (Vector3[])existingMesh.vertices.Clone();

        var mf = GetComponent<MeshFilter>();
        if (mf != null) mf.mesh = _clothMesh;

        var mc = GetComponent<MeshCollider>();
        if (mc != null) mc.sharedMesh = _clothMesh;

        Debug.Log($"[TaichiClothVR] '{name}' InitializeWithMesh: " +
                  $"{_vertices.Length} verts, {existingMesh.triangles.Length / 3} tris.");
    }

    // =========================================================================
    //  START
    // =========================================================================
    void Start()
    {
        // ── Mở socket NHẬN (Unity lắng nghe Python gửi mesh) ─────────────────
        try
        {
            _receiveEndPoint = new IPEndPoint(IPAddress.Any, receivePort);
            _receiver        = new UdpClient(receivePort);
            _receiver.Client.ReceiveBufferSize = 1024 * 1024; // 1 MB buffer
            Debug.Log($"[TaichiClothVR] '{name}' ✅ Receiver mở cổng {receivePort} thành công.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TaichiClothVR] '{name}' ❌ Không thể mở receivePort {receivePort}:\n{e.Message}\n" +
                           "→ Kiểm tra: (1) port có bị chiếm bởi process khác không, " +
                           "(2) receivePort có bằng PORT_SEND_TO_UNITY trong Python không?");
            enabled = false;
            return;
        }

        // ── Mở socket GỬI (Unity → Python grab) ──────────────────────────────
        try
        {
            _sendEndPoint = new IPEndPoint(IPAddress.Parse(serverIP), sendPort);
            _sender       = new UdpClient();
            Debug.Log($"[TaichiClothVR] '{name}' ✅ Sender sẵn sàng → {serverIP}:{sendPort}.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TaichiClothVR] '{name}' ❌ Không thể tạo sender:\n{e.Message}");
        }

        _meshCollider = GetComponent<MeshCollider>();

        if (triggerAction != null && triggerAction.action != null)
            triggerAction.action.Enable();

        if (grabSphere != null)
            grabSphere.gameObject.SetActive(false);

        // Nếu là piece sau khi cắt, _meshOverride đã được set bởi InitializeWithMesh()
        if (!_meshOverride)
            InitializeMesh();

        Debug.Log($"[TaichiClothVR] '{name}' sẵn sàng. Grid {width}×{height} ({width * height} verts).\n" +
                  $"  Nhận mesh từ Python: port {receivePort} (= PORT_SEND_TO_UNITY trong .py)\n" +
                  $"  Gửi grab tới Python: port {sendPort}   (= PORT_RECV_FROM_UNITY trong .py)");
    }

    // ── Khởi tạo lưới grid phẳng ban đầu ─────────────────────────────────────
    void InitializeMesh()
    {
        _clothMesh      = new Mesh();
        _clothMesh.MarkDynamic();
        _clothMesh.name = $"ClothMesh_{name}";
        GetComponent<MeshFilter>().mesh = _clothMesh;

        int totalVerts = width * height;
        _vertices = new Vector3[totalVerts];

        // Tạo flat plane để mesh hiện ra ngay trước khi nhận dữ liệu Python
        float dx = 15f / Mathf.Max(width  - 1, 1);
        float dy = 15f / Mathf.Max(height - 1, 1);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                _vertices[y * width + x] = new Vector3(x * dx - 7.5f, -y * dy + 7.5f, 0f);

        Vector2[] uvs = new Vector2[totalVerts];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                uvs[y * width + x] = new Vector2(
                    (float)x / (width  - 1),
                    1f - (float)y / (height - 1));

        int[] triangles = new int[(width - 1) * (height - 1) * 6];
        int t = 0;
        for (int y = 0; y < height - 1; y++)
            for (int x = 0; x < width - 1; x++)
            {
                int i = y * width + x;
                triangles[t++] = i;     triangles[t++] = i + 1;         triangles[t++] = i + width;
                triangles[t++] = i + 1; triangles[t++] = i + width + 1; triangles[t++] = i + width;
            }

        _clothMesh.vertices  = _vertices;
        _clothMesh.uv        = uvs;
        _clothMesh.triangles = triangles;
        _clothMesh.RecalculateNormals();
        _clothMesh.RecalculateBounds();

        if (_meshCollider != null) _meshCollider.sharedMesh = _clothMesh;

        Debug.Log($"[TaichiClothVR] '{name}' InitializeMesh: {totalVerts} verts, " +
                  $"{triangles.Length / 3} tris. Flat plane đã tạo — sẽ update khi nhận dữ liệu Python.");
    }

    // =========================================================================
    //  UPDATE
    // =========================================================================
    void Update()
    {
        // ── 1. NHẬN LƯỚI TỪ PYTHON ──────────────────────────────────────────
        while (_receiver != null && _receiver.Available > 0)
        {
            try
            {
                byte[] data        = _receiver.Receive(ref _receiveEndPoint);
                int    expectBytes = width * height * 12; // 3 floats × 4 bytes per vertex

                if (data.Length == expectBytes)
                {
                    for (int i = 0; i < _vertices.Length; i++)
                    {
                        _vertices[i] = new Vector3(
                            BitConverter.ToSingle(data, i * 12),
                            BitConverter.ToSingle(data, i * 12 + 4),
                            BitConverter.ToSingle(data, i * 12 + 8));
                    }
                    _clothMesh.vertices = _vertices;
                    _clothMesh.RecalculateNormals();
                    _clothMesh.RecalculateBounds();
                    if (_meshCollider != null) _meshCollider.sharedMesh = _clothMesh;
                    _packetsReceived++;
                }
                else
                {
                    Debug.LogWarning(
                        $"[TaichiClothVR] '{name}' ⚠️ Gói tin sai kích thước!\n" +
                        $"  Nhận được: {data.Length} bytes\n" +
                        $"  Mong đợi:  {expectBytes} bytes (width={width} × height={height} × 12)\n" +
                        $"  → Kiểm tra NW, NH trong Python có bằng width={width}, height={height} không.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[TaichiClothVR] '{name}' Lỗi nhận UDP: {e.Message}");
            }
        }

        // ── Debug log mỗi giây ───────────────────────────────────────────────
        if (debugLog)
        {
            _debugTimer += Time.deltaTime;
            if (_debugTimer >= 1f)
            {
                _debugTimer = 0f;
                if (_packetsReceived == 0)
                    Debug.LogWarning(
                        $"[TaichiClothVR] '{name}' ⚠️ 0 gói nhận được trong 1 giây!\n" +
                        $"  Kiểm tra:\n" +
                        $"  1. Python server đang chạy chưa? (python cloth_taichi_server.py)\n" +
                        $"  2. Python PORT_SEND_TO_UNITY = {receivePort} chưa?\n" +
                        $"  3. Tường lửa có chặn UDP port {receivePort} không?");
                else
                    Debug.Log($"[TaichiClothVR] '{name}' ✅ {_packetsReceived} gói/giây.");
                _packetsReceived = 0;
            }
        }

        // ── 2. LOGIC GRAB BẰNG TIA LASER ────────────────────────────────────
        bool triggerPressed = false;
        if (triggerAction != null && triggerAction.action != null)
            triggerPressed = triggerAction.action.ReadValue<float>() > 0.5f;

        if (triggerPressed && !_wasGrabbing && vrController != null)
        {
            Ray ray = new Ray(vrController.position, vrController.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, 20f))
            {
                if (hit.collider.gameObject == this.gameObject)
                {
                    _isGrabbing            = true;
                    _currentWorldTargetPos = hit.point;
                    _grabOffset            = vrController.InverseTransformPoint(hit.point);
                }
            }
        }
        else if (!triggerPressed)
        {
            _isGrabbing = false;
        }
        _wasGrabbing = triggerPressed;

        // ── 3. CẬP NHẬT QUẢ CẦU & TỌA ĐỘ LOCAL ────────────────────────────
        Vector3 pythonLocalTarget = Vector3.zero;
        if (_isGrabbing)
        {
            _currentWorldTargetPos = vrController.TransformPoint(_grabOffset);
            if (grabSphere != null)
            {
                grabSphere.position = _currentWorldTargetPos;
                grabSphere.gameObject.SetActive(true);
            }
            pythonLocalTarget = transform.InverseTransformPoint(_currentWorldTargetPos);
        }
        else
        {
            if (grabSphere != null) grabSphere.gameObject.SetActive(false);
        }

        // ── 4. GỬI DATA SANG PYTHON ──────────────────────────────────────────
        if (_sender != null)
        {
            try
            {
                byte[] sendData = new byte[16];
                Buffer.BlockCopy(BitConverter.GetBytes(_isGrabbing ? 1 : 0),    0, sendData, 0,  4);
                Buffer.BlockCopy(BitConverter.GetBytes(pythonLocalTarget.x),    0, sendData, 4,  4);
                Buffer.BlockCopy(BitConverter.GetBytes(pythonLocalTarget.y),    0, sendData, 8,  4);
                Buffer.BlockCopy(BitConverter.GetBytes(pythonLocalTarget.z),    0, sendData, 12, 4);
                _sender.Send(sendData, sendData.Length, _sendEndPoint);
            }
            catch (Exception e)
            {
                Debug.LogError($"[TaichiClothVR] '{name}' Lỗi gửi UDP: {e.Message}");
            }
        }
    }

    // =========================================================================
    void OnDestroy()
    {
        if (_receiver != null) { _receiver.Close(); _receiver = null; }
        if (_sender   != null) { _sender.Close();   _sender   = null; }
        if (triggerAction != null && triggerAction.action != null)
            triggerAction.action.Disable();
    }
}
