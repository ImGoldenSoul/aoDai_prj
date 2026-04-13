"""
cloth_taichi_server.py
======================
Taichi Cloth Physics Server — hỗ trợ cắt vải đệ quy (multi-piece).

═══ CỔNG UDP — PHẢI KHỚP VỚI TaichiClothVR.cs ══════════════════════════════
  PORT_SEND_TO_UNITY   = 5007  ←→  TaichiClothVR.receivePort = 5007
  PORT_RECV_FROM_UNITY = 5008  ←→  TaichiClothVR.sendPort    = 5008
  PORT_SPAWN_CMD       = 5099  ←→  TaichiPieceSpawner.spawnCommandPort = 5099
═════════════════════════════════════════════════════════════════════════════

Giao thức:
  Gói Mesh  (Python → Unity):  N×12 bytes  — N = NW×NH vertex, mỗi vertex 3×float32 (x,y,z)
  Gói Grab  (Unity → Python):  16 bytes    — struct 'ifff': grab_flag, hand_x, hand_y, hand_z
  Gói SPAWN (Unity → Python, port 5099):   — struct 'iiii': pw, ph, unity_recv, unity_send

Cách chạy:
  python cloth_taichi_server.py                          → server chính (vải gốc 30×30)
  python cloth_taichi_server.py piece 8 6 5100 5200      → piece server (tự động gọi)
"""

import sys
import taichi as ti
import numpy as np
import math
import socket
import struct
import time
import subprocess

# ─────────────────────────────────────────────────────────────────────────────
ti.init(arch=ti.cpu)   # Đổi ti.gpu nếu máy có GPU mạnh

# ─────────────────────────────────────────────────────────────────────────────
#  THAM SỐ VẬT LÝ
# ─────────────────────────────────────────────────────────────────────────────
MASS      = 0.01
K_STRETCH = 500.0
K_SHEAR   = 200.0
K_BEND    = 50.0
DAMPING   = 2.0
GRAVITY   = 9.8
DT        = 2e-4
SUBSTEPS  = 15
MAX_V     = 30.0

# ─────────────────────────────────────────────────────────────────────────────
#  CỔNG UDP CỦA SERVER CHÍNH
#  !! PHẢI KHỚP VỚI GIÁ TRỊ TRONG TaichiClothVR.cs !!
# ─────────────────────────────────────────────────────────────────────────────
PORT_SEND_TO_UNITY   = 5007   # Python gửi mesh → Unity nhận ở TaichiClothVR.receivePort
PORT_RECV_FROM_UNITY = 5008   # Python nhận grab ← Unity gửi từ TaichiClothVR.sendPort
PORT_SPAWN_CMD       = 5099   # Python nhận lệnh spawn piece mới từ Unity


# ─────────────────────────────────────────────────────────────────────────────
#  CLASS MÔ PHỎNG VẬT LÝ VẢI
# ─────────────────────────────────────────────────────────────────────────────
class ClothSimulator:
    """
    Mô phỏng vật lý lưới NW × NH bằng mass-spring.
    Hàng đầu tiên (i=0) được ghim cố định.
    """

    def __init__(self, nw: int, nh: int):
        self.NW   = nw
        self.NH   = nh
        self.DX   = 15.0 / max(nw - 1, 1)
        self.DY   = 15.0 / max(nh - 1, 1)
        self.DIAG = math.sqrt(2) * self.DX

        self.x    = ti.Vector.field(3, ti.f32, shape=(nh, nw))
        self.v    = ti.Vector.field(3, ti.f32, shape=(nh, nw))
        self.f    = ti.Vector.field(3, ti.f32, shape=(nh, nw))
        self.vpos = ti.Vector.field(3, ti.f32, shape=(nh * nw,))
        self.wind = ti.Vector.field(3, ti.f32, shape=())

        self._build_kernels()
        self._init_kernel()
        self.wind[None] = ti.Vector([0.0, 0.0, 0.0])

    def _build_kernels(self):
        nw, nh       = self.NW, self.NH
        dx, dy, diag = self.DX, self.DY, self.DIAG
        x, v, f      = self.x, self.v, self.f
        wind, vpos   = self.wind, self.vpos

        @ti.func
        def spring(xa, xb, rest, k):
            d = xb - xa
            l = d.norm()
            return k * (l - rest) / l * d if l > 1e-7 else d * 0.0

        @ti.kernel
        def _init():
            for i, j in x:
                x[i, j] = ti.Vector([j * dx - 0.5, 0.5 - i * dy, 0.0])
                v[i, j] = ti.Vector([0.0, 0.0, 0.0])

        @ti.kernel
        def _step():
            for i, j in x:
                fi = ti.Vector([0.0, -GRAVITY * MASS, 0.0])
                fi += wind[None]
                fi -= DAMPING * v[i, j]

                if i > 0:       fi += spring(x[i,j], x[i-1,j],   dy,    K_STRETCH)
                if i < nh-1:    fi += spring(x[i,j], x[i+1,j],   dy,    K_STRETCH)
                if j > 0:       fi += spring(x[i,j], x[i,j-1],   dx,    K_STRETCH)
                if j < nw-1:    fi += spring(x[i,j], x[i,j+1],   dx,    K_STRETCH)

                if i>0 and j>0:        fi += spring(x[i,j], x[i-1,j-1], diag, K_SHEAR)
                if i>0 and j<nw-1:     fi += spring(x[i,j], x[i-1,j+1], diag, K_SHEAR)
                if i<nh-1 and j>0:     fi += spring(x[i,j], x[i+1,j-1], diag, K_SHEAR)
                if i<nh-1 and j<nw-1:  fi += spring(x[i,j], x[i+1,j+1], diag, K_SHEAR)

                if i > 1:    fi += spring(x[i,j], x[i-2,j],   2*dy, K_BEND)
                if i < nh-2: fi += spring(x[i,j], x[i+2,j],   2*dy, K_BEND)
                if j > 1:    fi += spring(x[i,j], x[i,j-2],   2*dx, K_BEND)
                if j < nw-2: fi += spring(x[i,j], x[i,j+2],   2*dx, K_BEND)
                f[i, j] = fi

            for i, j in x:
                if i == 0:
                    v[i, j] = ti.Vector([0.0, 0.0, 0.0])
                else:
                    v[i, j] += DT * f[i, j] / MASS
                    speed = v[i, j].norm()
                    if speed > MAX_V:
                        v[i, j] = (v[i, j] / speed) * MAX_V
                    x[i, j] += DT * v[i, j]

        @ti.kernel
        def _update_vpos():
            for i, j in x:
                vpos[i * nw + j] = x[i, j]

        @ti.kernel
        def _grab(gi: int, gj: int, mx: float, my: float, mz: float):
            x[gi, gj].x = mx
            x[gi, gj].y = my
            x[gi, gj].z = mz
            v[gi, gj]   = ti.Vector([0.0, 0.0, 0.0])

        self._init_kernel    = _init
        self._step_kernel    = _step
        self._vpos_kernel    = _update_vpos
        self._grab_kernel    = _grab

    def step(self):
        self._step_kernel()

    def grab(self, gi, gj, mx, my, mz):
        self._grab_kernel(gi, gj, mx, my, mz)

    def get_bytes(self) -> bytes:
        self._vpos_kernel()
        return self.vpos.to_numpy().astype(np.float32).tobytes()

    def nearest(self, hx, hy, hz):
        """Tìm (i, j) của hạt gần tay nhất (bỏ qua hàng 0)."""
        pos  = self.x.to_numpy()
        dist = ((pos[1:,:,0]-hx)**2 + (pos[1:,:,1]-hy)**2 + (pos[1:,:,2]-hz)**2)
        idx  = np.unravel_index(np.argmin(dist), dist.shape)
        return idx[0] + 1, idx[1]


# ─────────────────────────────────────────────────────────────────────────────
#  HÀM VÒNG LẶP CHÍNH (dùng chung cho main server và piece server)
# ─────────────────────────────────────────────────────────────────────────────
def run_server(nw, nh, port_send, port_recv, label="Server"):
    """
    nw, nh      — kích thước lưới
    port_send   — Python GỬI mesh đến Unity ở port này  (= TaichiClothVR.receivePort)
    port_recv   — Python NHẬN grab từ Unity ở port này  (= TaichiClothVR.sendPort)
    """
    UDP_IP = "127.0.0.1"
    sim    = ClothSimulator(nw, nh)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((UDP_IP, port_recv))
    sock.setblocking(False)

    print(f"✅ [{label}] Physics Server ({nw}×{nh}) running.")
    print(f"   Gửi mesh đến Unity  : {UDP_IP}:{port_send}  (= TaichiClothVR.receivePort)")
    print(f"   Nhận grab từ Unity  : {UDP_IP}:{port_recv}  (= TaichiClothVR.sendPort)")

    is_grabbing        = False
    gi = gj            = -1
    hx = hy = hz       = 0.0
    frames             = 0

    while True:
        # 1. Nhận grab từ Unity
        try:
            data = None
            while True:
                try:
                    data, _ = sock.recvfrom(1024)
                except BlockingIOError:
                    break
            if data and len(data) >= 16:
                flag, hx, hy, hz = struct.unpack('ifff', data[:16])
                if flag == 1:
                    if not is_grabbing:
                        gi, gj = sim.nearest(hx, hy, hz)
                    is_grabbing = True
                else:
                    is_grabbing = False
        except Exception as e:
            print(f"[{label}] Lỗi nhận: {e}")

        # 2. Vật lý
        for _ in range(SUBSTEPS):
            sim.step()
            if is_grabbing:
                sim.grab(gi, gj, hx, hy, hz)

        # 3. Gửi mesh
        try:
            sock.sendto(sim.get_bytes(), (UDP_IP, port_send))
        except BlockingIOError:
            pass

        # Debug log mỗi 144 frame (~1 giây)
        frames += 1
        if frames % 144 == 0:
            print(f"[{label}] ▶ frame {frames}, grabbing={is_grabbing}")

        time.sleep(1 / 144)


# ─────────────────────────────────────────────────────────────────────────────
#  SERVER CHÍNH: vật lý vải gốc + lắng nghe lệnh SPAWN piece mới từ Unity
# ─────────────────────────────────────────────────────────────────────────────
def run_main_server():
    NW, NH = 30, 30
    UDP_IP = "127.0.0.1"

    sim = ClothSimulator(NW, NH)

    # Socket vật lý chính
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((UDP_IP, PORT_RECV_FROM_UNITY))
    sock.setblocking(False)

    # Socket lắng nghe lệnh SPAWN từ Unity (TaichiPieceSpawner.cs)
    spawn_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    spawn_sock.bind((UDP_IP, PORT_SPAWN_CMD))
    spawn_sock.setblocking(False)

    print("=" * 60)
    print(f"✅ [Main] Taichi Physics Server ({NW}×{NH}) khởi động.")
    print(f"   Gửi mesh đến Unity  : {UDP_IP}:{PORT_SEND_TO_UNITY}")
    print(f"   Nhận grab từ Unity  : {UDP_IP}:{PORT_RECV_FROM_UNITY}")
    print(f"   Nhận lệnh SPAWN     : {UDP_IP}:{PORT_SPAWN_CMD}")
    print()
    print("   Trong Unity, TaichiClothVR phải có:")
    print(f"     receivePort = {PORT_SEND_TO_UNITY}  (Unity nhận mesh từ Python)")
    print(f"     sendPort    = {PORT_RECV_FROM_UNITY}  (Unity gửi grab tới Python)")
    print("=" * 60)

    is_grabbing = False
    gi = gj     = -1
    hx = hy = hz = 0.0
    frames       = 0

    while True:
        # ── Lắng nghe SPAWN ──────────────────────────────────────────────────
        try:
            data = None
            while True:
                try:
                    data, _ = spawn_sock.recvfrom(64)
                except BlockingIOError:
                    break
            if data and len(data) >= 16:
                pw, ph, unity_recv, unity_send = struct.unpack('iiii', data[:16])
                # unity_recv: port Unity dùng để NHẬN mesh từ piece server
                # unity_send: port Unity dùng để GỬI grab đến piece server
                # → piece Python: gửi đến unity_recv, nghe từ unity_send
                print(f"[Main] SPAWN piece {pw}×{ph} "
                      f"→ piece_send={unity_recv}, piece_recv={unity_send}")
                subprocess.Popen([
                    sys.executable, __file__,
                    "piece",
                    str(pw), str(ph),
                    str(unity_recv),  # port Python gửi đến Unity
                    str(unity_send),  # port Python nhận từ Unity
                ])
        except Exception as e:
            print(f"[Main] SPAWN error: {e}")

        # ── Nhận grab chính ──────────────────────────────────────────────────
        try:
            data = None
            while True:
                try:
                    data, _ = sock.recvfrom(1024)
                except BlockingIOError:
                    break
            if data and len(data) >= 16:
                flag, hx, hy, hz = struct.unpack('ifff', data[:16])
                if flag == 1:
                    if not is_grabbing:
                        gi, gj = sim.nearest(hx, hy, hz)
                    is_grabbing = True
                else:
                    is_grabbing = False
        except Exception as e:
            print(f"[Main] Lỗi nhận grab: {e}")

        # ── Vật lý ───────────────────────────────────────────────────────────
        for _ in range(SUBSTEPS):
            sim.step()
            if is_grabbing:
                sim.grab(gi, gj, hx, hy, hz)

        # ── Gửi mesh ─────────────────────────────────────────────────────────
        try:
            sock.sendto(sim.get_bytes(), (UDP_IP, PORT_SEND_TO_UNITY))
        except BlockingIOError:
            pass

        frames += 1
        if frames % 144 == 0:
            print(f"[Main] ▶ frame {frames}, grabbing={is_grabbing}")

        time.sleep(1 / 144)


# ─────────────────────────────────────────────────────────────────────────────
#  ENTRY POINT
# ─────────────────────────────────────────────────────────────────────────────
if __name__ == "__main__":
    """
    python cloth_taichi_server.py                        → server chính
    python cloth_taichi_server.py piece pw ph psnd prcv  → piece server (tự động)
    """
    if len(sys.argv) >= 6 and sys.argv[1] == "piece":
        pw   = int(sys.argv[2])
        ph   = int(sys.argv[3])
        psnd = int(sys.argv[4])  # Python gửi đến Unity
        prcv = int(sys.argv[5])  # Python nhận từ Unity
        run_server(pw, ph, psnd, prcv, label=f"Piece {pw}x{ph}@recv:{prcv}")
    else:
        run_main_server()
