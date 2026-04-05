import taichi as ti
import numpy as np
import math
import socket
import struct
import time

# Chạy trên CPU để đảm bảo tương thích, nếu máy bạn có Card rời mạnh có thể đổi thành ti.gpu
ti.init(arch=ti.cpu)

# ── Kích thước lưới vải ─────────────────────────────────────────────────────
NW, NH  = 30, 30          # số hạt ngang × dọc
DX = DY = 1.0 / (NW - 1)  # khoảng cách giữa các hạt
DIAG    = math.sqrt(2) * DX

# ── Tham số vật lý ──────────────────────────────────────────────────────────
MASS       = 0.01    # kg / hạt
K_STRETCH  = 500.0   # độ cứng lò xo kết cấu  [N/m]
K_SHEAR    = 200.0   # độ cứng lò xo cắt chéo [N/m]
K_BEND     = 50.0    # bending stiffness      [N/m]
DAMPING    = 2.0     # Cản không khí (Chống giật)
GRAVITY    = 9.8     # m/s²
DT         = 2e-4    # bước thời gian [s]
SUBSTEPS   = 15      # bước vật lý / frame
MAX_V      = 30.0    # Giới hạn tốc độ tối đa (Trị nổ lưới)

# ── Fields ───────────────────────────────────────────────────────────────────
x = ti.Vector.field(3, ti.f32, shape=(NH, NW))  # vị trí
v = ti.Vector.field(3, ti.f32, shape=(NH, NW))  # vận tốc
f = ti.Vector.field(3, ti.f32, shape=(NH, NW))  # lực
vpos = ti.Vector.field(3, ti.f32, shape=NH * NW) # mảng 1D để nén gửi qua mạng

wind = ti.Vector.field(3, ti.f32, shape=())

# ── Kernels ──────────────────────────────────────────────────────────────────
@ti.func
def spring(xa, xb, rest, k):
    d = xb - xa
    l = d.norm()
    return k * (l - rest) / l * d if l > 1e-7 else d * 0.0

@ti.kernel
def init():
    for i, j in x:
        x[i, j] = ti.Vector([j * DX - 0.5, 0.5 - i * DY, 0.0])
        v[i, j] = ti.Vector([0.0, 0.0, 0.0])

@ti.kernel
def step():
    for i, j in x:
        fi = ti.Vector([0.0, -GRAVITY * MASS, 0.0])
        fi += wind[None]
        fi -= DAMPING * v[i, j]
        
        if i > 0:    fi += spring(x[i,j], x[i-1,j],   DY, K_STRETCH)
        if i < NH-1: fi += spring(x[i,j], x[i+1,j],   DY, K_STRETCH)
        if j > 0:    fi += spring(x[i,j], x[i,j-1],   DX, K_STRETCH)
        if j < NW-1: fi += spring(x[i,j], x[i,j+1],   DX, K_STRETCH)
        
        if i>0 and j>0:       fi += spring(x[i,j], x[i-1,j-1], DIAG, K_SHEAR)
        if i>0 and j<NW-1:    fi += spring(x[i,j], x[i-1,j+1], DIAG, K_SHEAR)
        if i<NH-1 and j>0:    fi += spring(x[i,j], x[i+1,j-1], DIAG, K_SHEAR)
        if i<NH-1 and j<NW-1: fi += spring(x[i,j], x[i+1,j+1], DIAG, K_SHEAR)
        
        if i > 1:    fi += spring(x[i,j], x[i-2,j],   2*DY, K_BEND)
        if i < NH-2: fi += spring(x[i,j], x[i+2,j],   2*DY, K_BEND)
        if j > 1:    fi += spring(x[i,j], x[i,j-2],   2*DX, K_BEND)
        if j < NW-2: fi += spring(x[i,j], x[i,j+2],   2*DX, K_BEND)
        f[i, j] = fi

    for i, j in x:
        if i == 0:   # Hàng đầu được ghim cố định
            v[i, j] = ti.Vector([0.0, 0.0, 0.0])
        else:
            v[i, j] += DT * f[i, j] / MASS
            
            # Trị nổ lưới
            speed = v[i, j].norm()
            if speed > MAX_V:
                v[i, j] = (v[i, j] / speed) * MAX_V
                
            x[i, j] += DT * v[i, j]

@ti.kernel
def update_vpos():
    # Đổ mảng 2D x sang mảng 1D vpos để gửi cho Unity
    for i, j in x:
        vpos[i * NW + j] = x[i, j]

@ti.kernel
def enforce_grab_3d(gi: int, gj: int, mx: float, my: float, mz: float):
    # Khóa hạt tại tọa độ 3D của Controller
    x[gi, gj].x = mx
    x[gi, gj].y = my
    x[gi, gj].z = mz
    v[gi, gj] = ti.Vector([0.0, 0.0, 0.0])

# ── Thiết lập UDP Server ────────────────────────────────────────────────────
UDP_IP = "127.0.0.1"
PORT_SEND_TO_UNITY = 5005  # Cổng Unity đang lắng nghe
PORT_RECV_FROM_UNITY = 5006 # Cổng Python này lắng nghe Unity

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.bind((UDP_IP, PORT_RECV_FROM_UNITY))
sock.setblocking(False) # Chế độ Non-blocking để vòng lặp vật lý không bị dừng chờ mạng

# ── Khởi tạo ─────────────────────────────────────────────────────────────────
init()
wind[None] = ti.Vector([0.0, 0.0, 0.0])

print(f"✅ Taichi Physics Server đang chạy.")
print(f"  -> Nhận lệnh từ Unity ở port: {PORT_RECV_FROM_UNITY}")
print(f"  -> Gửi lưới tới Unity ở port: {PORT_SEND_TO_UNITY}")
print("Đang chờ Unity kết nối...")

is_grabbing = False
grabbed_i, grabbed_j = -1, -1
hand_x, hand_y, hand_z = 0.0, 0.0, 0.0

# Vòng lặp chính (Không dùng ti.ui.Window nữa)
while True:
    # 1. Nhận gói tin từ Unity
    try:
        data = None
        # Vét sạch buffer mạng để lấy gói tin mới nhất (tránh độ trễ do dồn ứ gói tin cũ)
        while True:
            try:
                data, addr = sock.recvfrom(1024)
            except BlockingIOError:
                break
        
        # Nếu có dữ liệu từ Unity
        if data:
            # Giao thức mong đợi: 1 số nguyên (is_grabbing: 1=có, 0=không) và 3 số thực (x, y, z)
            # Kích thước: 4 bytes (int) + 12 bytes (3x float) = 16 bytes
            if len(data) >= 16:
                grab_flag, hand_x, hand_y, hand_z = struct.unpack('ifff', data[:16])
                
                if grab_flag == 1:
                    if not is_grabbing:
                        # Vừa mới bóp Trigger: Quét tìm hạt gần Controller 3D nhất
                        pos = x.to_numpy()
                        # Tính bình phương khoảng cách 3D (bỏ qua hàng trên cùng i=0)
                        dist_sq = (pos[1:, :, 0] - hand_x)**2 + (pos[1:, :, 1] - hand_y)**2 + (pos[1:, :, 2] - hand_z)**2
                        min_idx = np.unravel_index(np.argmin(dist_sq), dist_sq.shape)
                        grabbed_i, grabbed_j = min_idx[0] + 1, min_idx[1]
                        is_grabbing = True
                else:
                    is_grabbing = False
    
    except Exception as e:
        print(f"Lỗi nhận dữ liệu: {e}")

    # 2. Tính toán Vật lý
    for _ in range(SUBSTEPS):
        step()
        if is_grabbing:
            enforce_grab_3d(grabbed_i, grabbed_j, hand_x, hand_y, hand_z)

    # 3. Gói và Gửi dữ liệu lưới sang Unity
    update_vpos()
    pos_array = vpos.to_numpy()
    
    # Ép kiểu về float32 (C# sẽ đọc là float) và chuyển thành chuỗi byte
    byte_data = pos_array.astype(np.float32).tobytes()
    
    try:
        sock.sendto(byte_data, (UDP_IP, PORT_SEND_TO_UNITY))
    except BlockingIOError:
        pass # Nếu mạng bị nghẽn thì bỏ qua frame này, gửi frame sau
        
    # Cho CPU nghỉ ngơi một chút xíu (khoảng 144 FPS) để không ăn 100% CPU
    time.sleep(1/144)