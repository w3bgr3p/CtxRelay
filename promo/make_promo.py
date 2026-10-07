"""Промо-ролик CtxDeck, 20 c. Кадры рисуются PIL (суперсэмплинг x2), кодирует ffmpeg.

python make_promo.py --track <m4a> --out CtxDeck_promo.mp4
Клип берётся из трека так, чтобы дроп (53.96 c) пришёлся на 7-ю долю (4.667 c) ролика.
"""
import argparse
import functools
import math
import subprocess
import sys
from multiprocessing import Pool
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

W, H = 1280, 720          # единицы дизайна
S = 2                     # суперсэмплинг, рендер в 2560x1440
OUT_W, OUT_H = 1920, 1080
FPS, DUR = 30, 20.0
LOGO = str(Path(__file__).resolve().parent.parent / 'icon.png')   # иконка приложения из корня репо
BEAT = 60 / 90            # 90 bpm
DROP = 7 * BEAT           # 4.667 c: дроп в ролике
DROP_IN_TRACK = 53.96

BG = (14, 16, 20)
ACCENT = (76, 194, 255)
GREEN, RED = (40, 170, 90), (220, 60, 60)
TEXT, DIM = (240, 240, 240), (150, 152, 158)
MENU_BG, MENU_HOVER, MENU_BORDER, MENU_SEP = (43, 43, 43), (61, 61, 61), (70, 70, 70), (68, 68, 68)


def U(v):
    return int(round(v * S))


def clamp(x, a=0.0, b=1.0):
    return max(a, min(b, x))


def lerp(a, b, t):
    return a + (b - a) * t


def mix(c1, c2, t):
    return tuple(int(round(lerp(a, b, t))) for a, b in zip(c1, c2))


def prog(t, t0, dur):
    return clamp((t - t0) / dur)


def out_cubic(x):
    return 1 - (1 - x) ** 3


def in_out(x):
    return x * x * (3 - 2 * x)


def out_back(x, k=1.70158):
    x -= 1
    return 1 + (k + 1) * x ** 3 + k * x ** 2


@functools.lru_cache(maxsize=None)
def font(name, size):
    return ImageFont.truetype(f"C:/Windows/Fonts/{name}.ttf", max(1, int(round(size * S))))


def text(d, xy, s, name, size, fill, alpha=1.0, anchor="la"):
    if alpha <= 0.003:
        return
    d.text((U(xy[0]), U(xy[1])), s, font=font(name, size), fill=(*fill, int(255 * clamp(alpha))), anchor=anchor)


@functools.lru_cache(maxsize=None)
def radial_mask(r_px):
    """L-маска мягкого радиального свечения (центр 255 -> край 0). Считается в 1/4 разрешения."""
    q = max(8, r_px // 4)
    n = 2 * q
    y, x = np.mgrid[0:n, 0:n].astype(np.float32)
    d = np.sqrt((x - q) ** 2 + (y - q) ** 2) / q
    a = np.clip(1 - d, 0, 1) ** 2.2
    return Image.fromarray((a * 255).astype(np.uint8), "L").resize((2 * r_px, 2 * r_px), Image.BILINEAR)


def glow(img, cx, cy, r, color, k):
    """Свечение радиуса r (ед. дизайна), яркость k 0..1."""
    if k <= 0.004 or r < 2:
        return
    m = radial_mask(U(r)).point(lambda v: int(v * clamp(k)))
    img.paste(Image.new("RGB", m.size, color), (U(cx) - U(r), U(cy) - U(r)), m)


def background(t):
    img = Image.new("RGB", (U(W), U(H)), BG)
    glow(img, 250 + 60 * math.sin(t * 0.35), 120 + 30 * math.cos(t * 0.3), 560, (20, 70, 110), 0.55)
    glow(img, 1050 + 70 * math.cos(t * 0.28), 620 + 40 * math.sin(t * 0.4), 520, (60, 40, 110), 0.45)
    return img


def tray_icon(d, img, cx, cy, r, color, label, alpha=1.0, pulse=0.0, font_scale=1.0):
    if r < 2:
        return
    glow(img, cx, cy, r * (2.1 + 0.25 * pulse), color, 0.5 * alpha * (0.7 + 0.3 * pulse))
    d.ellipse((U(cx - r), U(cy - r), U(cx + r), U(cy + r)), fill=(*color, int(255 * alpha)))
    size = r * (0.95 if len(label) < 3 else 0.7) * font_scale
    text(d, (cx, cy + r * 0.04), label, "segoeuib", size, (255, 255, 255), alpha, "mm")


TILE_BG, TILE_EDGE = (24, 24, 28), (52, 54, 60)


def tile_icon(d, img, cx, cy, size, color, label, alpha=1.0, pulse=0.0):
    """Иконка трея как в приложении: тёмная плитка и крупные цветные цифры остатка."""
    if size < 4:
        return
    glow(img, cx, cy, size * (1.1 + 0.15 * pulse), color, 0.45 * alpha * (0.7 + 0.3 * pulse))
    h = size / 2
    d.rounded_rectangle((U(cx - h), U(cy - h), U(cx + h), U(cy + h)), U(size * 0.2),
                        fill=(*TILE_BG, int(255 * alpha)), outline=(*TILE_EDGE, int(255 * alpha)), width=max(1, U(size * 0.012)))
    fs = size * {1: 1.0, 2: 0.78}.get(len(label), 0.55)
    text(d, (cx, cy + size * 0.03), label, "segoeuib", fs, color, alpha, "mm")


def ring_rr(d, cx, cy, size, color, alpha, width=3):
    if alpha <= 0.003:
        return
    h = size / 2
    d.rounded_rectangle((U(cx - h), U(cy - h), U(cx + h), U(cy + h)), U(size * 0.2),
                        outline=(*color, int(255 * clamp(alpha))), width=max(1, U(width)))


@functools.lru_cache(maxsize=None)
def logo_src():
    return Image.open(LOGO).convert("RGBA").resize((640, 640), Image.LANCZOS)


def draw_logo(img, cx, cy, r, alpha=1.0, pulse=0.0):
    """Логотип приложения (icon.png), r — радиус в ед. дизайна."""
    if r < 3 or alpha <= 0.003:
        return
    glow(img, cx, cy, r * 1.9, (50, 130, 255), 0.5 * alpha * (0.65 + 0.35 * pulse))
    n = U(2 * r)
    lg = logo_src().resize((n, n), Image.LANCZOS)
    mask = lg.getchannel("A").point(lambda v: int(v * clamp(alpha)))
    img.paste(lg.convert("RGB"), (U(cx) - n // 2, U(cy) - n // 2), mask)


def ring(d, cx, cy, r, color, alpha, width=3):
    if alpha <= 0.003:
        return
    d.ellipse((U(cx - r), U(cy - r), U(cx + r), U(cy + r)), outline=(*color, int(255 * clamp(alpha))), width=max(1, U(width)))


def taskbar(d, t, icon_color, icon_label):
    a = out_cubic(prog(t, 0.1, 0.6))
    y0 = H - 46 * a
    d.rectangle((0, U(y0), U(W), U(H)), fill=(22, 24, 29))
    d.line((0, U(y0), U(W), U(y0)), fill=(44, 47, 54), width=U(1))
    for i, x in enumerate((1040, 1068, 1096, 1124)):
        d.ellipse((U(x - 7), U(y0 + 23 - 7), U(x + 7), U(y0 + 23 + 7)), fill=(60 + 6 * i, 63 + 6 * i, 70 + 6 * i))
    d.rounded_rectangle((U(1160 - 13), U(y0 + 23 - 13), U(1160 + 13), U(y0 + 23 + 13)), U(4), fill=TILE_BG, outline=TILE_EDGE, width=U(1))
    text(d, (1160, y0 + 23.5), icon_label, "segoeuib", {1: 17, 2: 13}.get(len(icon_label), 9), icon_color, 1, "mm")
    text(d, (1250, y0 + 23), "11:56", "segoeui", 13, DIM, 1, "mm")


# ----------------------------------------------------------------- меню

MENU_W = 400
GLYPH = {"refresh": "", "folder": "", "startup": "", "power": "", "check": ""}
# (kind, высота): строки меню сверху вниз
ROWS = [("cap", 34), ("cap", 30), ("sep", 11), ("acc", 38), ("acc", 38), ("sep", 11),
        ("item", 34), ("item", 34), ("item", 34), ("sep", 11), ("item", 34)]
MENU_PAD = 8
MENU_H = MENU_PAD * 2 + sum(h for _, h in ROWS)
MS = 1.32                                      # меню увеличено при вставке
MENU_RIGHT, MENU_BOTTOM = 1215, H - 56
TILE_CX = MENU_RIGHT - MENU_W * MS / 2         # центр плитки (ед. дизайна)
TILE_CY = MENU_BOTTOM - MENU_H * MS / 2


def row_top(i):
    return MENU_PAD + sum(h for _, h in ROWS[:i])


@functools.lru_cache(maxsize=None)
def menu_shadow():
    m = 50
    base = Image.new("L", (U(MENU_W + 2 * m), U(MENU_H + 2 * m)), 0)
    ImageDraw.Draw(base).rounded_rectangle((U(m), U(m + 8), U(m + MENU_W), U(m + 8 + MENU_H)), U(10), fill=150)
    return base.filter(ImageFilter.GaussianBlur(U(18)))


def menu_tile(t, state, hover_row=None):
    """RGBA-плитка меню. state: имя активного, цвет/подпись активного, остатки."""
    m = 50
    tile = Image.new("RGBA", (U(MENU_W + 2 * m), U(MENU_H + 2 * m)), (0, 0, 0, 0))
    tile.paste(Image.new("RGBA", tile.size, (0, 0, 0, 255)), (0, 0), menu_shadow())
    d = ImageDraw.Draw(tile)
    d.rounded_rectangle((U(m), U(m), U(m + MENU_W), U(m + MENU_H)), U(10), fill=MENU_BG, outline=MENU_BORDER, width=U(1))
    reveal = [7.40, 7.55, 7.7, 8.0, 8.67, 8.8, 9.33, 9.45, 9.57, 9.7, 9.8]
    acc_i = 0
    for i, (kind, h) in enumerate(ROWS):
        a = out_cubic(prog(t, reveal[i], 0.3))
        if a <= 0:
            continue
        y = m + row_top(i) + (1 - a) * 10
        fg = lambda c: mix(MENU_BG, c, a)
        if hover_row == i:
            d.rounded_rectangle((U(m + 4), U(y + 1), U(m + MENU_W - 4), U(y + h - 1)), U(5), fill=MENU_HOVER)
        cy = y + h / 2
        if kind == "cap":
            if i == 0:
                d.ellipse((U(m + 14), U(cy - 4), U(m + 22), U(cy + 4)), fill=fg(state["head_color"]))
                text(d, (m + 40, cy), state["head"], "segoeui", 14.5, fg(DIM), 1, "lm")
            else:
                text(d, (m + 40, cy), "Лимиты на 11:56", "segoeui", 14.5, fg(DIM), 1, "lm")
        elif kind == "sep":
            d.line((U(m + 1), U(y + h / 2), U(m + MENU_W - 1), U(y + h / 2)), fill=fg(MENU_SEP), width=U(1))
        elif kind == "acc":
            acc = state["accounts"][acc_i]
            acc_i += 1
            if acc["active"]:
                text(d, (m + 18, cy), GLYPH["check"], "segmdl2", 14, fg(ACCENT), 1, "mm")
            else:
                d.ellipse((U(m + 14), U(cy - 4), U(m + 22), U(cy + 4)), fill=fg(acc["dot"]))
            text(d, (m + 40, cy), acc["name"], "segoeui", 15, fg(TEXT), 1, "lm")
            text(d, (m + MENU_W - 16, cy), acc["right"], "segoeui", 14, fg(DIM), 1, "rm")
        else:
            label, glyph, right = item_for(i)
            text(d, (m + 18, cy), GLYPH[glyph], "segmdl2", 14, fg(TEXT), 1, "mm")
            text(d, (m + 40, cy), label, "segoeui", 15, fg(TEXT), 1, "lm")
            if right:
                text(d, (m + MENU_W - 16, cy), right, "segoeui", 14, fg(DIM), 1, "rm")
    return tile


def item_for(i):
    return {6: ("Обновить лимиты", "refresh", ""), 7: ("Открыть папку", "folder", ""),
            8: ("Автозапуск", "startup", "вкл"), 10: ("Выход", "power", "")}[i]


def paste_tile(img, tile, x, y, scale=1.0, alpha=1.0):
    """Вставка плитки с центром масштабирования в её середине. x,y — левый верх при scale=1 (ед. дизайна)."""
    if alpha <= 0.003:
        return
    w, h = tile.size
    if abs(scale - 1) > 1e-3:
        tile = tile.resize((max(1, int(w * scale)), max(1, int(h * scale))), Image.BILINEAR)
    px = U(x) + (w - tile.size[0]) // 2
    py = U(y) + (h - tile.size[1]) // 2
    mask = tile.getchannel("A")
    if alpha < 0.999:
        mask = mask.point(lambda v: int(v * alpha))
    img.paste(tile.convert("RGB"), (px, py), mask)


# ----------------------------------------------------------------- сцены

T_MOVE1, T_CLOSE, T_SWAP, T_DONE, T_END = 7.33, 14.15, 14.4, 16.667, 17.33
POS_A, POS_B = (640, 290, 120), (320, 330, 110)
ROW_CLICK = 3                                  # строка "personal"
CLICK_T = 14.0


def caption(d, t, lines, t_in, t_out, y=500, color=TEXT, size=27):
    a = prog(t, t_in, 0.3) * (1 - prog(t, t_out - 0.3, 0.3))
    for k, s in enumerate(lines):
        text(d, (POS_B[0], y + k * 38 + (1 - out_cubic(prog(t, t_in, 0.4))) * 10), s, "seguisb" if k == 0 else "segoeui",
             size, color if k == 0 else DIM, a, "mm")


def cursor(d, x, y, alpha, press=1.0):
    if alpha <= 0.003:
        return
    pts = [(0, 0), (0, 17), (4.5, 13), (8, 20), (11, 18.5), (7.5, 12), (13, 12)]
    p = [(U(x + px * press), U(y + py * press)) for px, py in pts]
    d.polygon(p, fill=(255, 255, 255, int(255 * alpha)), outline=(20, 20, 20, int(255 * alpha)))


def render(i):
    t = i / FPS
    img = background(t)
    d = ImageDraw.Draw(img, "RGBA")

    # --- состояния
    k = out_cubic(prog(t, T_DONE, 0.55))
    icon_color = mix(RED, GREEN, k)
    icon_label = str(int(round(93 * k)))
    w = in_out(prog(t, T_MOVE1, 0.7)) - in_out(prog(t, T_END, 0.8))
    if t < DROP:
        pulse = 0.5 + 0.5 * math.sin(t * math.pi)
        phase = None
    else:
        phase = ((t - DROP) % BEAT) / BEAT
        pulse = math.exp(-4 * phase)

    taskbar(d, t, icon_color, icon_label)

    # --- меню
    m_in = prog(t, T_MOVE1, 0.25)
    m_close = prog(t, T_CLOSE, 0.3)
    if T_MOVE1 <= t <= T_CLOSE + 0.31:
        state = {
            "head": "work · осталось 0%", "head_color": RED,
            "accounts": [
                {"name": "personal", "active": False, "dot": GREEN, "right": "5h 93% · нед 98%"},
                {"name": "work", "active": True, "dot": RED, "right": "лимит до 12:01"},
            ]}
        tile = menu_tile(t, state, ROW_CLICK if 13.33 <= t < T_CLOSE else None)
        sc = (0.94 + 0.06 * out_cubic(prog(t, T_MOVE1, 0.45))) - 0.04 * m_close
        slide = (1 - out_cubic(prog(t, T_MOVE1, 0.5))) * 40
        paste_tile(img, tile, TILE_CX - (MENU_W + 100) / 2 + slide, TILE_CY - (MENU_H + 100) / 2, sc * MS, m_in * (1 - m_close))

    # --- плитка трея: герой хука и сцены с меню
    TS = 2 * POS_B[2] * 0.95
    swapping = T_SWAP <= t < T_DONE
    ia = 0.8 if swapping else 1.0
    if 0.2 <= t < DROP + 0.05:
        tile_icon(d, img, POS_A[0], POS_A[1], 2 * POS_A[2] * 0.95 * out_back(prog(t, 0.2, 0.7)), icon_color, icon_label, 1.0, pulse)
    elif T_MOVE1 + 0.2 <= t < T_END + 0.4:
        cx, cy = POS_B[0], POS_B[1]
        fade = 1 - prog(t, T_END, 0.3)
        if t < T_CLOSE:
            ring_rr(d, cx, cy, TS * (1.1 + 0.6 * phase), icon_color, 0.45 * (1 - phase) * fade, 2.5)
        tile_icon(d, img, cx, cy, TS * out_back(prog(t, T_MOVE1 + 0.2, 0.5)), icon_color, icon_label, ia * fade, pulse)
        if swapping:
            rr = TS * 0.67
            ring(d, cx, cy, rr, (70, 74, 82), 0.8, 3)
            a0 = t * 380
            d.arc((U(cx - rr), U(cy - rr), U(cx + rr), U(cy + rr)), a0, a0 + 100, fill=(*ACCENT, 255), width=U(5))
        if t >= T_DONE:
            p = prog(t, T_DONE, 0.8)
            ring_rr(d, cx, cy, TS * (1.05 + 0.9 * out_cubic(p)), GREEN, 0.8 * (1 - p) * fade, 4)

    # --- логотип приложения: слэм на дропе -> в угол -> снова в центр
    if t >= DROP:
        big = 125 * (1 + 0.35 * (1 - out_cubic(prog(t, DROP, 0.4)))) if t < T_MOVE1 else 125
        lr = lerp(big, 22, w)
        lx, ly = lerp(640, 80, w), lerp(290, 50, w)
        la = prog(t, DROP, 0.1)
        if w < 0.3 and phase is not None:
            ring(d, lx, ly, lr * (1.1 + 0.55 * phase), (90, 160, 255), 0.4 * (1 - phase), 2.5)
        draw_logo(img, lx, ly, lr, la, pulse)

    # --- тексты: хук
    if t < DROP:
        fade = 1 - prog(t, DROP - 0.3, 0.28)
        text(d, (640, 470), "Лимит Codex кончился.", "segoeuib", 52, TEXT, prog(t, 0.5, 0.5) * fade, "mm")
        text(d, (640, 540), "…а задача ещё нет.", "segoeui", 28, DIM, prog(t, 2.0, 0.5) * fade, "mm")
    # --- вордмарк
    fade_out = 1 - prog(t, T_MOVE1, 0.4)
    if DROP <= t < T_MOVE1 + 0.4:
        sc = 1 + 0.25 * (1 - out_cubic(prog(t, DROP, 0.35)))
        text(d, (640, 470), "CtxDeck", "segoeuib", 84 * sc, TEXT, prog(t, DROP, 0.12) * fade_out, "mm")
        text(d, (640, 545), "Лимиты Codex и смена аккаунта — из трея", "segoeui", 28, DIM, prog(t, DROP + 0.4, 0.4) * fade_out, "mm")
    if t >= T_END:
        sc = 1 + 0.2 * (1 - out_cubic(prog(t, T_END + 0.5, 0.4)))
        text(d, (640, 470), "CtxDeck", "segoeuib", 84 * sc, TEXT, prog(t, T_END + 0.5, 0.3), "mm")
        text(d, (640, 545), "Один клик — другой аккаунт.", "segoeui", 28, DIM, prog(t, T_END + 0.9, 0.4), "mm")
    text(d, (112, 50), "CtxDeck", "segoeuib", 26, TEXT, prog(t, 7.8, 0.4) * (1 - prog(t, T_END, 0.3)), "lm")

    # --- подписи под иконкой
    caption(d, t, ["Остаток лимита — прямо в иконке"], 7.6, 10.67)
    caption(d, t, ["Все аккаунты и лимиты", "по правому клику"], 10.67, 14.0)
    caption(d, t, ["Перезапуск ChatGPT…"], T_SWAP + 0.1, T_DONE, color=ACCENT)
    caption(d, t, ["Готово. Остаток 93%"], T_DONE + 0.1, T_END + 0.1, color=GREEN)

    # --- курсор и клик
    rx = TILE_CX + 90
    ry = TILE_CY + (row_top(ROW_CLICK) + ROWS[ROW_CLICK][1] / 2 - MENU_H / 2) * MS
    mv = in_out(prog(t, 12.0, 1.33))
    cxp, cyp = lerp(1250, rx, mv), lerp(430, ry, mv)
    ca = prog(t, 12.0, 0.3) * (1 - prog(t, 14.3, 0.3))
    press = 0.86 if CLICK_T <= t < CLICK_T + 0.15 else 1.0
    if CLICK_T <= t < CLICK_T + 0.6:
        p = prog(t, CLICK_T, 0.6)
        ring(d, rx, ry, 8 + 34 * out_cubic(p), ACCENT, 0.7 * (1 - p), 2)
    cursor(d, cxp, cyp, ca, press)

    # --- флэш дропа и затемнение в конце
    if t >= DROP:
        f = 0.55 * math.exp(-9 * (t - DROP))
        if f > 0.01:
            d.rectangle((0, 0, U(W), U(H)), fill=(255, 255, 255, int(255 * f)))
    fo = prog(t, 18.6, 1.4)
    if fo > 0:
        d.rectangle((0, 0, U(W), U(H)), fill=(0, 0, 0, int(255 * fo)))

    arr = np.asarray(img)
    return cv2.resize(arr, (OUT_W, OUT_H), interpolation=cv2.INTER_AREA).tobytes()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--track", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--frames", type=int, default=int(FPS * DUR), help="для отладки: рендерить только N кадров")
    ap.add_argument("--still", type=float, help="сохранить один кадр (сек) в PNG рядом с --out и выйти")
    a = ap.parse_args()
    if a.still is not None:
        frame = np.frombuffer(render(int(a.still * FPS)), np.uint8).reshape(OUT_H, OUT_W, 3)
        cv2.imwrite(str(Path(a.out).with_suffix(".png")), cv2.cvtColor(frame, cv2.COLOR_RGB2BGR))
        return 0
    start = DROP_IN_TRACK - DROP
    cmd = ["ffmpeg", "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{OUT_W}x{OUT_H}", "-r", str(FPS), "-i", "-",
           "-ss", f"{start:.3f}", "-t", f"{DUR}", "-i", a.track,
           "-map", "0:v", "-map", "1:a:0", "-af", "afade=t=in:st=0:d=0.4,afade=t=out:st=18.6:d=1.4",
           "-c:v", "libx264", "-crf", "16", "-preset", "medium", "-pix_fmt", "yuv420p",
           "-c:a", "aac", "-b:a", "192k", "-shortest", a.out]
    ff = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    try:
        with Pool(3) as pool:
            for n, fr in enumerate(pool.imap(render, range(a.frames), chunksize=2)):
                ff.stdin.write(fr)
                if n % 60 == 0:
                    print(f"frame {n}/{a.frames}", file=sys.stderr, flush=True)
        ff.stdin.close()
        return ff.wait()
    except BaseException:
        ff.kill()
        raise


if __name__ == "__main__":
    sys.exit(main())
