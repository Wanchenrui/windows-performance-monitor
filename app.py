# -*- coding: utf-8 -*-
"""电脑性能实时监控 0.2。

0.3.0 变更：
  - 使用 Windows 原生 API 后端采集，不再周期性创建 PowerShell 进程；
  - 冻结语言无关 contract v1，产品版本与契约版本正式解耦；
  - 使用互斥体、当前用户状态文件和随机 instanceId 识别已有实例；
  - 托盘依赖缺失时降级为控制台运行，不再直接崩溃。
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import sys
import threading
import time
import uuid
import webbrowser
from datetime import datetime
from logging.handlers import RotatingFileHandler
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import urlopen

from perf_monitor import APP_NAME, APP_VERSION, SERVICE_ID
from perf_monitor.collector import NativeWindowsCollector
from perf_monitor.sampling import FixedPeriodSampler
from perf_monitor.server import create_server
from perf_monitor.store import MetricStore


HOST = "127.0.0.1"
DEFAULT_PORT = 7700
DEFAULT_SAMPLE_INTERVAL_SECONDS = 1.0
DEFAULT_HISTORY_MINUTES = 60.0

# PyInstaller --onefile 会把静态资源解压到 sys._MEIPASS。
BASE_DIR = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent))
STATIC_DIR = BASE_DIR / "static"


def _env_number(name: str, default: float) -> float:
    raw = os.environ.get(name)
    if raw is None:
        return default
    try:
        return float(raw)
    except ValueError:
        return default


def _port(value: str) -> int:
    port = int(value)
    if not 1 <= port <= 65535:
        raise argparse.ArgumentTypeError("端口必须在 1～65535 之间")
    return port


def _sample_interval(value: str) -> float:
    interval = float(value)
    if not 0.25 <= interval <= 60.0:
        raise argparse.ArgumentTypeError("采样周期必须在 0.25～60 秒之间")
    return interval


def _history_minutes(value: str) -> float:
    minutes = float(value)
    if not 1.0 <= minutes <= 1440.0:
        raise argparse.ArgumentTypeError("历史窗口必须在 1～1440 分钟之间")
    return minutes


def _positive_seconds(value: str) -> float:
    seconds = float(value)
    if seconds <= 0:
        raise argparse.ArgumentTypeError("秒数必须大于 0")
    return seconds


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=f"{APP_NAME} {APP_VERSION}（仅监听本机回环地址）"
    )
    parser.add_argument(
        "--port",
        type=_port,
        default=int(_env_number("PERF_MONITOR_PORT", DEFAULT_PORT)),
        help=f"本地 HTTP 端口，默认 {DEFAULT_PORT}",
    )
    parser.add_argument(
        "--sample-interval",
        type=_sample_interval,
        default=_env_number(
            "PERF_MONITOR_SAMPLE_INTERVAL",
            DEFAULT_SAMPLE_INTERVAL_SECONDS,
        ),
        help="采样周期（秒），默认 1",
    )
    parser.add_argument(
        "--history-minutes",
        type=_history_minutes,
        default=_env_number(
            "PERF_MONITOR_HISTORY_MINUTES",
            DEFAULT_HISTORY_MINUTES,
        ),
        help="内存历史窗口（分钟），默认 60",
    )
    parser.add_argument(
        "--no-browser",
        action="store_true",
        help="启动时不自动打开浏览器",
    )
    parser.add_argument(
        "--no-tray",
        action="store_true",
        help="不创建系统托盘图标，在控制台运行",
    )
    parser.add_argument(
        "--log-level",
        choices=("DEBUG", "INFO", "WARNING", "ERROR"),
        default=os.environ.get("PERF_MONITOR_LOG_LEVEL", "INFO").upper(),
        help="日志级别，默认 INFO",
    )
    parser.add_argument(
        "--exit-after-seconds",
        type=_positive_seconds,
        default=None,
        help=argparse.SUPPRESS,
    )
    args = parser.parse_args(argv)

    # argparse 的 type 不会处理默认值，因此环境变量也要执行同样的边界校验。
    args.port = _port(str(args.port))
    args.sample_interval = _sample_interval(str(args.sample_interval))
    args.history_minutes = _history_minutes(str(args.history_minutes))
    return args


def configure_logging(level: str) -> Path | None:
    """建立轮转日志；目录不可写时仍保留控制台日志。"""

    formatter = logging.Formatter(
        "%(asctime)s %(levelname)s %(name)s: %(message)s"
    )
    handlers: list[logging.Handler] = []
    if sys.stderr is not None:
        console = logging.StreamHandler()
        console.setFormatter(formatter)
        handlers.append(console)

    log_path: Path | None = None
    try:
        local_app_data = Path(
            os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local")
        )
        log_dir = local_app_data / "PerfMonitor" / "logs"
        log_dir.mkdir(parents=True, exist_ok=True)
        log_path = log_dir / "perf-monitor.log"
        file_handler = RotatingFileHandler(
            log_path,
            maxBytes=1_048_576,
            backupCount=3,
            encoding="utf-8",
        )
        file_handler.setFormatter(formatter)
        handlers.append(file_handler)
    except OSError:
        log_path = None

    logging.basicConfig(
        level=getattr(logging, level),
        handlers=handlers or [logging.NullHandler()],
        force=True,
    )
    return log_path


def probe_existing_instance(url: str, expected_instance_id: str) -> bool:
    """同时匹配状态文件中的随机实例 ID，拒绝只伪造 service 的监听者。"""

    try:
        with urlopen(f"{url}/api/health", timeout=0.8) as response:
            payload = json.loads(response.read().decode("utf-8"))
        return (
            payload.get("service") == SERVICE_ID
            and payload.get("instanceId") == expected_instance_id
        )
    except (OSError, ValueError, HTTPError, URLError, json.JSONDecodeError):
        return False


class SingleInstanceGuard:
    """用 Windows Local 命名互斥体保护当前用户会话中的单实例。"""

    ERROR_ALREADY_EXISTS = 183

    def __init__(self, mutex_name: str = "Local\\PerfMonitor") -> None:
        self.acquired = True
        self._handle = None
        self._kernel32 = None
        if sys.platform != "win32":
            return

        import ctypes
        from ctypes import wintypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CreateMutexW.argtypes = (
            wintypes.LPVOID,
            wintypes.BOOL,
            wintypes.LPCWSTR,
        )
        kernel32.CreateMutexW.restype = wintypes.HANDLE
        kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
        kernel32.CloseHandle.restype = wintypes.BOOL

        ctypes.set_last_error(0)
        handle = kernel32.CreateMutexW(
            None,
            False,
            mutex_name,
        )
        if not handle:
            raise OSError(ctypes.get_last_error(), "创建单实例互斥体失败")
        self._kernel32 = kernel32
        self._handle = handle
        if ctypes.get_last_error() == self.ERROR_ALREADY_EXISTS:
            self.acquired = False
            self.close()

    def close(self) -> None:
        if self._handle is not None and self._kernel32 is not None:
            self._kernel32.CloseHandle(self._handle)
            self._handle = None


def instance_state_path() -> Path:
    local_app_data = Path(
        os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local")
    )
    return local_app_data / "PerfMonitor" / "instance.json"


def write_instance_state(path: Path, port: int, instance_id: str) -> None:
    """原子写入已有实例发现信息；不包含令牌或用户数据。"""

    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    payload = {
        "service": SERVICE_ID,
        "instanceId": instance_id,
        "pid": os.getpid(),
        "port": port,
        "startedAt": datetime.now().astimezone().isoformat(
            timespec="seconds"
        ),
    }
    temporary.write_text(
        json.dumps(payload, ensure_ascii=False),
        encoding="utf-8",
    )
    os.replace(temporary, path)


def read_instance_identity(path: Path) -> tuple[str, str] | None:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
        port = int(payload["port"])
        instance_id = str(payload["instanceId"])
        if (
            payload.get("service") != SERVICE_ID
            or not 1 <= port <= 65535
            or uuid.UUID(hex=instance_id).hex != instance_id
        ):
            return None
        return f"http://{HOST}:{port}", instance_id
    except (
        AttributeError,
        OSError,
        ValueError,
        TypeError,
        KeyError,
        json.JSONDecodeError,
    ):
        return None


def read_instance_url(path: Path) -> str | None:
    identity = read_instance_identity(path)
    return identity[0] if identity else None


def remove_own_instance_state(path: Path) -> None:
    """只删除由当前 PID 写入的瞬态实例文件。"""

    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
        if int(payload.get("pid", -1)) == os.getpid():
            path.unlink(missing_ok=True)
    except (OSError, ValueError, TypeError, json.JSONDecodeError):
        pass


def discover_existing_instance(
    path: Path,
    timeout: float = 2.0,
) -> str | None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        identity = read_instance_identity(path)
        if (
            identity
            and probe_existing_instance(identity[0], identity[1])
        ):
            url, _ = identity
            return url
        time.sleep(0.1)
    return None


def show_error(title: str, message: str) -> None:
    """控制台与 windowed EXE 都能看到的明确启动错误。"""

    if sys.stderr is not None:
        print(f"{title}：{message}", file=sys.stderr)
    if (
        sys.platform == "win32"
        and os.environ.get("PERF_MONITOR_SUPPRESS_DIALOGS") != "1"
    ):
        try:
            import ctypes

            ctypes.windll.user32.MessageBoxW(0, message, title, 0x10)
        except Exception:
            pass


def open_page(url: str, logger: logging.Logger) -> None:
    try:
        if not webbrowser.open(url):
            logger.warning("系统未确认浏览器已打开，请手动访问 %s", url)
    except Exception:
        logger.exception("打开浏览器失败，请手动访问 %s", url)


def make_icon():
    """生成托盘图标，不依赖外部图片文件。"""

    from PIL import Image, ImageDraw

    image = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.ellipse(
        [2, 2, 62, 62],
        fill="#0b0e14",
        outline="#34d399",
        width=3,
    )
    draw.line(
        [14, 40, 24, 40, 28, 24, 34, 52, 38, 32, 42, 40, 52, 40],
        fill="#34d399",
        width=4,
        joint="curve",
    )
    return image


def run_tray(
    url: str,
    stop_event: threading.Event,
    logger: logging.Logger,
) -> bool:
    """运行托盘事件循环；依赖不可用时返回 False 以触发控制台降级。"""

    try:
        import pystray
    except ImportError:
        logger.warning(
            "未安装 pystray，已降级为控制台模式；运行安装脚本可启用托盘"
        )
        return False

    def tray_open(icon=None, item=None) -> None:
        open_page(url, logger)

    def tray_quit(icon, item) -> None:
        stop_event.set()
        icon.stop()

    try:
        menu = pystray.Menu(
            pystray.MenuItem("打开监控页面", tray_open, default=True),
            pystray.MenuItem("退出", tray_quit),
        )
        icon = pystray.Icon(
            SERVICE_ID,
            make_icon(),
            APP_NAME,
            menu,
        )
        icon.run()
        return True
    except Exception:
        logger.exception("托盘初始化失败，已降级为控制台模式")
        return False


def wait_in_console(
    stop_event: threading.Event,
    server_thread: threading.Thread,
) -> None:
    try:
        while not stop_event.wait(0.5):
            if not server_thread.is_alive():
                raise RuntimeError("本地 HTTP 服务线程意外退出")
    except KeyboardInterrupt:
        stop_event.set()


def run_primary_instance(
    args: argparse.Namespace,
    logger: logging.Logger,
    log_path: Path | None,
    url: str,
    instance_id: str,
) -> int:
    state_path = instance_state_path()
    store = MetricStore(
        history_window_seconds=args.history_minutes * 60.0,
        sample_interval_seconds=args.sample_interval,
        instance_id=instance_id,
    )
    try:
        server = create_server(HOST, args.port, store, STATIC_DIR)
    except OSError as exc:
        logger.error(
            "端口 %d 无法绑定：%s",
            args.port,
            exc,
        )
        show_error(
            "电脑性能监控启动失败",
            f"本地端口 {args.port} 已被其他程序占用。\n"
            "请关闭占用程序，或使用 --port 指定其他端口。",
        )
        return 2

    collector = NativeWindowsCollector(process_limit=5)
    sampler = FixedPeriodSampler(
        collector,
        store,
        args.sample_interval,
        logger=logger,
    )
    stop_event = threading.Event()
    server_thread = threading.Thread(
        target=server.serve_forever,
        name="local-http",
        daemon=True,
    )

    logger.info(
        "%s %s 启动；地址=%s，采样周期=%.3fs，历史窗口=%.1fmin",
        APP_NAME,
        APP_VERSION,
        url,
        args.sample_interval,
        args.history_minutes,
    )
    if log_path:
        logger.info("日志文件：%s", log_path)

    try:
        server_thread.start()
        sampler.start()
        if args.exit_after_seconds is not None:
            exit_timer = threading.Timer(
                args.exit_after_seconds,
                stop_event.set,
            )
            exit_timer.daemon = True
            exit_timer.start()
        try:
            write_instance_state(state_path, args.port, instance_id)
        except OSError as exc:
            logger.warning("写入实例发现信息失败：%s", exc)
        if not args.no_browser:
            # 服务线程已经开始监听；只在此入口打开一次。
            open_page(url, logger)

        used_tray = False
        if not args.no_tray:
            used_tray = run_tray(url, stop_event, logger)
        if not used_tray and not stop_event.is_set():
            logger.info("控制台模式运行中，按 Ctrl+C 退出")
            wait_in_console(stop_event, server_thread)
    finally:
        stop_event.set()
        sampler.stop()
        server.shutdown()
        server.server_close()
        server_thread.join(timeout=5.0)
        remove_own_instance_state(state_path)
        logger.info("程序已退出")
    return 0


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    log_path = configure_logging(args.log_level)
    logger = logging.getLogger("perf-monitor")
    url = f"http://{HOST}:{args.port}"

    required_assets = ("index.html", "dashboard.css", "dashboard.js")
    missing_assets = [
        name for name in required_assets if not (STATIC_DIR / name).is_file()
    ]
    if missing_assets:
        message = f"缺少静态资源：{', '.join(missing_assets)}"
        logger.error(message)
        show_error("电脑性能监控启动失败", message)
        return 3

    try:
        instance_guard = SingleInstanceGuard()
    except OSError as exc:
        logger.error("创建单实例保护失败：%s", exc)
        show_error("电脑性能监控启动失败", str(exc))
        return 2

    if not instance_guard.acquired:
        existing_url = discover_existing_instance(instance_state_path())
        if existing_url:
            logger.info("检测到已有实例：%s", existing_url)
            if not args.no_browser:
                open_page(existing_url, logger)
            return 0
        message = (
            "检测到已有实例，但其健康接口在 2 秒内未响应。"
            "请稍后重试，或在任务管理器中检查 perf-monitor 进程。"
        )
        logger.error(message)
        show_error("电脑性能监控启动失败", message)
        return 2

    try:
        return run_primary_instance(
            args,
            logger,
            log_path,
            url,
            uuid.uuid4().hex,
        )
    finally:
        instance_guard.close()


if __name__ == "__main__":
    raise SystemExit(main())
