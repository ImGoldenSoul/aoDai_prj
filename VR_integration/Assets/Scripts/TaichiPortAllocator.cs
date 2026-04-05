using UnityEngine;

/// <summary>
/// Bộ cấp phát port UDP tự động, thread-safe trong Unity main thread.
/// Mỗi piece Taichi mới cần 1 cặp port (recv, send) riêng để không
/// xung đột với miếng vải gốc hoặc các mảnh khác.
///
/// Port mặc định:
///   - Cloth gốc dùng receivePort=5005, sendPort=5006 (theo TaichiClothVR.cs gốc)
///   - Các piece bắt đầu từ 5100 (recv) và 5200 (send), tăng dần theo 2.
/// </summary>
public static class TaichiPortAllocator
{
    private static int _nextRecv = 5100;
    private static int _nextSend = 5200;

    /// <summary>Trả về port nhận kế tiếp và tăng bộ đếm lên 2.</summary>
    public static int NextReceivePort()
    {
        int p = _nextRecv;
        _nextRecv += 2;
        return p;
    }

    /// <summary>Trả về port gửi kế tiếp và tăng bộ đếm lên 2.</summary>
    public static int NextSendPort()
    {
        int p = _nextSend;
        _nextSend += 2;
        return p;
    }

    /// <summary>Reset về giá trị khởi đầu (hữu ích khi reload scene).</summary>
    public static void Reset()
    {
        _nextRecv = 5100;
        _nextSend = 5200;
    }
}
