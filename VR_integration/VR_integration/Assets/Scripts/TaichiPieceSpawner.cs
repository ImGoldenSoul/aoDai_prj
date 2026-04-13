using UnityEngine;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// TaichiPieceSpawner — gắn lên cùng GameObject với TaichiClothVR của piece.
///
/// Ngay khi Start() chạy (tức là piece vừa được spawn), nó gửi 1 gói UDP
/// tới Python MainServer (PORT_SPAWN_CMD = 5099) để Python biết cần
/// khởi động một sub-process vật lý cho mảnh này.
///
/// Gói SPAWN (16 bytes, struct 'iiii'):
///   [0] pw         — grid width của piece
///   [1] ph         — grid height của piece
///   [2] recvPort   — port Unity sẽ NHẬN mesh từ Python  (TaichiClothVR.receivePort)
///   [3] sendPort   — port Unity sẽ GỬI grab tới Python  (TaichiClothVR.sendPort)
///
/// Component này tự hủy sau khi gửi xong (không cần tồn tại lâu dài).
/// </summary>
[RequireComponent(typeof(TaichiClothVR))]
public class TaichiPieceSpawner : MonoBehaviour
{
    [Tooltip("IP của Python server (thường là 127.0.0.1)")]
    public string pythonServerIP = "127.0.0.1";

    [Tooltip("Port SPAWN của Python MainServer (mặc định 5099)")]
    public int spawnCommandPort = 5099;

    void Start()
    {
        var cloth = GetComponent<TaichiClothVR>();
        if (cloth == null)
        {
            Destroy(this);
            return;
        }

        try
        {
            // Đóng gói lệnh SPAWN: pw, ph, recvPort, sendPort
            byte[] data = new byte[16];
            System.Buffer.BlockCopy(System.BitConverter.GetBytes(cloth.width),       0, data, 0,  4);
            System.Buffer.BlockCopy(System.BitConverter.GetBytes(cloth.height),      0, data, 4,  4);
            System.Buffer.BlockCopy(System.BitConverter.GetBytes(cloth.receivePort), 0, data, 8,  4);
            System.Buffer.BlockCopy(System.BitConverter.GetBytes(cloth.sendPort),    0, data, 12, 4);

            using var udp = new UdpClient();
            var ep = new IPEndPoint(IPAddress.Parse(pythonServerIP), spawnCommandPort);
            udp.Send(data, data.Length, ep);

            Debug.Log($"[TaichiPieceSpawner] SPAWN gửi cho Python: " +
                      $"grid {cloth.width}×{cloth.height} " +
                      $"recv:{cloth.receivePort} send:{cloth.sendPort}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[TaichiPieceSpawner] Lỗi gửi SPAWN: {e.Message}");
        }

        // Tự hủy sau khi đã gửi xong
        Destroy(this);
    }
}
