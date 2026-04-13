import taichi as ti
import numpy as np
import math

ti.init(arch=ti.cpu)

# ── Kích thước lưới vải ─────────────────────────────────────────────────────
NW, NH  = 30, 30          # số hạt ngang × dọc
DX = DY = 1.0 / (NW - 1)  # khoảng cách giữa các hạt
DIAG    = math.sqrt(2) * DX

# ── Tham số vật lý ──────────────────────────────────────────────────────────
MASS       = 0.01    # kg / hạt
K_STRETCH  = 500.0   # độ cứng lò xo kết cấu  [N/m]
K_SHEAR    = 200.0   # độ cứng lò xo cắt chéo [N/m]
K_BEND     = 50.0    # bending stiffness       [N/m]
DAMPING    = 1.0     # CẬP NHẬT: Tăng cản gió để dập tắt dao động thừa, tránh giật lưới
GRAVITY    = 9.8     # m/s²
DT         = 2e-4    # bước thời gian [s]
SUBSTEPS   = 15      # bước vật lý / frame

# ── Fields ───────────────────────────────────────────────────────────────────
x = ti.Vector.field(3, ti.f32, shape=(NH, NW))  # vị trí
v = ti.Vector.field(3, ti.f32, shape=(NH, NW))  # vận tốc
f = ti.Vector.field(3, ti.f32, shape=(NH, NW))  # lực

n_verts = NH * NW
n_tris  = 2 * (NH - 1) * (NW - 1)
vpos    = ti.Vector.field(3, ti.f32, shape=n_verts)
vnorm   = ti.Vector.field(3, ti.f32, shape=n_verts)
idx     = ti.field(ti.i32, shape=n_tris * 3)

wind    = ti.Vector.field(3, ti.f32, shape=())

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
def build_idx():
    for i, j in ti.ndrange(NH - 1, NW - 1):
        c  = i * (NW - 1) + j
        v0 = i * NW + j;  v1 = (i+1)*NW + j
        v2 = i * NW + j+1; v3 = (i+1)*NW + j+1
        idx[c*6+0]=v0; idx[c*6+1]=v1; idx[c*6+2]=v2
        idx[c*6+3]=v1; idx[c*6+4]=v3; idx[c*6+5]=v2

@ti.kernel
def step():
    # Tính lực
    for i, j in x:
        fi = ti.Vector([0.0, -GRAVITY * MASS, 0.0])
        fi += wind[None]
        fi -= DAMPING * v[i, j]
        # Structural
        if i > 0:    fi += spring(x[i,j], x[i-1,j],   DY, K_STRETCH)
        if i < NH-1: fi += spring(x[i,j], x[i+1,j],   DY, K_STRETCH)
        if j > 0:    fi += spring(x[i,j], x[i,j-1],   DX, K_STRETCH)
        if j < NW-1: fi += spring(x[i,j], x[i,j+1],   DX, K_STRETCH)
        # Shear
        if i>0 and j>0:       fi += spring(x[i,j], x[i-1,j-1], DIAG, K_SHEAR)
        if i>0 and j<NW-1:    fi += spring(x[i,j], x[i-1,j+1], DIAG, K_SHEAR)
        if i<NH-1 and j>0:    fi += spring(x[i,j], x[i+1,j-1], DIAG, K_SHEAR)
        if i<NH-1 and j<NW-1: fi += spring(x[i,j], x[i+1,j+1], DIAG, K_SHEAR)
        # Bending (skip-1)
        if i > 1:    fi += spring(x[i,j], x[i-2,j],   2*DY, K_BEND)
        if i < NH-2: fi += spring(x[i,j], x[i+2,j],   2*DY, K_BEND)
        if j > 1:    fi += spring(x[i,j], x[i,j-2],   2*DX, K_BEND)
        if j < NW-2: fi += spring(x[i,j], x[i,j+2],   2*DX, K_BEND)
        f[i, j] = fi

    # Tích phân & ghim hàng trên cùng
    for i, j in x:
        if i == 0:   # hàng đầu được ghim cố định
            v[i, j] = ti.Vector([0.0, 0.0, 0.0])
        else:
            v[i, j] += DT * f[i, j] / MASS
            
            # CẬP NHẬT: TUYỆT CHIÊU TRỊ NỔ LƯỚI - Khóa Vận Tốc
            max_v = 30.0 # Giới hạn tốc độ bay tối đa (m/s)
            speed = v[i, j].norm()
            if speed > max_v:
                v[i, j] = (v[i, j] / speed) * max_v
            
            x[i, j] += DT * v[i, j]
            
            if x[i, j].y < -1.0:   # sàn ảo
                x[i, j].y = -1.0
                v[i, j].y = 0.0

@ti.kernel
def update_mesh():
    for i, j in x:
        vpos[i * NW + j] = x[i, j]
    for k in vnorm:
        vnorm[k] = ti.Vector([0.0, 0.0, 0.0])
    for i, j in ti.ndrange(NH-1, NW-1):
        v0=i*NW+j; v1=(i+1)*NW+j; v2=i*NW+j+1; v3=(i+1)*NW+j+1
        p0=vpos[v0]; p1=vpos[v1]; p2=vpos[v2]; p3=vpos[v3]
        n1 = (p1-p0).cross(p2-p0)
        n2 = (p3-p1).cross(p2-p1)
        ti.atomic_add(vnorm[v0], n1)
        ti.atomic_add(vnorm[v1], n1+n2)
        ti.atomic_add(vnorm[v2], n1+n2)
        ti.atomic_add(vnorm[v3], n2)
    for k in vnorm:
        n = vnorm[k]; l = n.norm()
        vnorm[k] = n/l if l > 1e-6 else ti.Vector([0.0,1.0,0.0])

# Kernel ghim vị trí hạt khi chuột kéo (Biến hạt thành Kinematic)
@ti.kernel
def enforce_grab(gi: int, gj: int, mx: float, my: float):
    x[gi, gj].x = mx
    x[gi, gj].y = my
    v[gi, gj] = ti.Vector([0.0, 0.0, 0.0])

# ── Main ─────────────────────────────────────────────────────────────────────
build_idx()
init()
wind[None] = ti.Vector([0.0, 0.0, 0.0])

window = ti.ui.Window("Cloth Simulation - Anti-Explosion", (800, 800), vsync=True)
canvas = window.get_canvas()
scene = window.get_scene()
camera = ti.ui.Camera()
camera.position(0, 0, 2.5)
camera.lookat(0, 0, 0)

wind_on = False
t = 0.0

# Các biến phục vụ kéo thả chuột
is_grabbing = False
grabbed_i, grabbed_j = -1, -1

while window.running:
    # Lấy tọa độ chuột trên màn hình (0.0 đến 1.0)
    mouse_u, mouse_v = window.get_cursor_pos()
    
    # Ánh xạ tọa độ 2D của màn hình ra không gian 3D tại Z=0
    mx = (mouse_u - 0.5) * 2.1
    my = (mouse_v - 0.5) * 2.1

    for e in window.get_events(ti.ui.PRESS):
        if e.key in ('w', 'W'):
            wind_on = not wind_on
            print("Gió:", "BẬT" if wind_on else "TẮT")
        if e.key in ('r', 'R'):
            init(); t = 0.0

    # Xử lý Logic Kéo Thả Chuột
    if window.is_pressed(ti.ui.LMB):
        if not is_grabbing:
            # Nếu vừa bấm chuột: Quét tìm hạt vải gần nhất bằng numpy
            pos = x.to_numpy()
            # pos[1:, :, ...] bỏ qua hàng i=0 vì đang bị ghim cố định trên xà
            dist_sq = (pos[1:, :, 0] - mx)**2 + (pos[1:, :, 1] - my)**2
            min_idx = np.unravel_index(np.argmin(dist_sq), dist_sq.shape)
            
            # Lấy index thực tế (cộng thêm 1 do đã cắt mảng ở trên)
            grabbed_i, grabbed_j = min_idx[0] + 1, min_idx[1]
            is_grabbing = True
    else:
        is_grabbing = False

    wind[None] = ti.Vector([0.0, 0.0, 0.5 * math.sin(t * 1.5)]) if wind_on else ti.Vector([0.0, 0.0, 0.0])

    for _ in range(SUBSTEPS):
        step()
        # NGAY SAU KHI tính vật lý, ta ép lại vị trí hạt đang bị túm
        if is_grabbing:
            enforce_grab(grabbed_i, grabbed_j, mx, my)
        t += DT

    update_mesh()
    camera.track_user_inputs(window, movement_speed=0.02, hold_key=ti.ui.RMB)
    scene.set_camera(camera)
    scene.ambient_light([0.4, 0.4, 0.4])
    scene.point_light(pos=(1, 2, 2), color=(1, 1, 1))
    
    # Bật show_wireframe=True để hiển thị lưới đan
    scene.mesh(vpos, indices=idx, normals=vnorm, color=(0.8, 0.7, 0.9), two_sided=True, show_wireframe=True)
    
    canvas.set_background_color((0.1, 0.1, 0.1))
    canvas.scene(scene)
    window.show()