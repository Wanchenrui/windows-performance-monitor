"""仅监听回环地址的版本化本地 HTTP 接口。"""

from __future__ import annotations

import json
import logging
import socket
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit

from . import API_VERSION, APP_VERSION, SERVICE_ID


class LocalThreadingHTTPServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = False

    def server_bind(self) -> None:
        # Windows 默认的地址复用语义可能允许多个监听者竞争同一端口。
        # 独占绑定使端口冲突成为确定、可报告的启动错误。
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            self.socket.setsockopt(
                socket.SOL_SOCKET,
                socket.SO_EXCLUSIVEADDRUSE,
                1,
            )
        super().server_bind()


class DashboardHandler(BaseHTTPRequestHandler):
    store: Any
    static_dir: Path
    logger = logging.getLogger(__name__)

    _STATIC_FILES = {
        "/": ("index.html", "text/html; charset=utf-8"),
        "/index.html": ("index.html", "text/html; charset=utf-8"),
        "/dashboard.css": ("dashboard.css", "text/css; charset=utf-8"),
        "/dashboard.js": (
            "dashboard.js",
            "application/javascript; charset=utf-8",
        ),
    }

    def _security_headers(self) -> None:
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "DENY")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header(
            "Content-Security-Policy",
            "default-src 'self'; script-src 'self'; "
            "style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
            "connect-src 'self'; object-src 'none'; frame-ancestors 'none'",
        )

    def _send_bytes(
        self,
        status: int,
        body: bytes,
        content_type: str,
    ) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self._security_headers()
        self.end_headers()
        self.wfile.write(body)

    def _send_json(self, status: int, payload: dict[str, Any]) -> None:
        body = json.dumps(
            payload,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
        ).encode("utf-8")
        self._send_bytes(
            status,
            body,
            "application/json; charset=utf-8",
        )

    def do_GET(self) -> None:
        path = urlsplit(self.path).path
        if path == "/api/stats":
            self._send_json(200, self.store.get_payload())
            return
        if path == "/api/health":
            payload = self.store.get_payload()
            self._send_json(
                200,
                {
                    "service": SERVICE_ID,
                    "appVersion": APP_VERSION,
                    "apiVersion": API_VERSION,
                    "sequence": payload["sequence"],
                    "health": payload["health"],
                },
            )
            return
        if path in self._STATIC_FILES:
            filename, content_type = self._STATIC_FILES[path]
            try:
                body = (self.static_dir / filename).read_bytes()
            except OSError as exc:
                self.logger.error("读取静态资源失败 %s: %s", filename, exc)
                self._send_json(
                    500,
                    {
                        "error": "static_asset_unavailable",
                        "asset": filename,
                    },
                )
                return
            self._send_bytes(200, body, content_type)
            return

        self._send_json(404, {"error": "not_found"})

    def log_message(self, fmt: str, *args: object) -> None:
        self.logger.debug("%s - %s", self.client_address[0], fmt % args)


def create_server(
    host: str,
    port: int,
    store: Any,
    static_dir: Path,
) -> LocalThreadingHTTPServer:
    """创建绑定指定快照存储的服务实例。"""

    class BoundDashboardHandler(DashboardHandler):
        pass

    BoundDashboardHandler.store = store
    BoundDashboardHandler.static_dir = static_dir
    return LocalThreadingHTTPServer((host, port), BoundDashboardHandler)
