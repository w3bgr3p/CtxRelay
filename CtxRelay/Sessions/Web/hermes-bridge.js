import { host } from '@hermes/plugin-sdk';

// Installed through Hermes Desktop's supported runtime plugin loader.
export default {
  id: 'cdx-session-bridge', name: 'CtxRelay session opener',
  register(ctx) {
    const desktop = window.hermesDesktop;
    const path = __REQUEST_PATH__;
    let busy = false, last = '', incoming = null;
    const read = async () => {
      if (busy || !desktop || host.state.gateway.get() !== 'open') return;
      busy = true;
      try {
        let request = incoming;
        try {
          const stored = JSON.parse((await desktop.readFileText(path)).text);
          if (!request || (request.sessionId === stored.sessionId && request.profile === stored.profile)) request = stored;
        } catch { if (!request) return; }
        incoming = null;
        if (!request.requestId || request.requestId === last || request.expires < Date.now()) return;
        last = request.requestId;
        await host.openSession(request.sessionId, { profile: request.profile, intent: 'in-place' });
        const ack = { ...request, route: window.location.hash, activeProfile: host.state.profile.get(), openedAt: Date.now() };
        await desktop.writeTextFile(path + '.ack', JSON.stringify(ack));
        console.info('[CtxRelay] opened session', JSON.stringify(ack));
      } catch (error) {
        console.error('[CtxRelay] session open failed', String(error));
      } finally { busy = false; }
    };
    const timer = setInterval(read, 1000);
    ctx.onDispose(() => clearInterval(timer));
    const unsubscribe = desktop.onDeepLink?.(payload => {
      if (payload.kind !== 'cdx-session' || !payload.name) return;
      incoming = { requestId: 'link-' + Date.now(), sessionId: payload.name, profile: payload.params.profile || 'default', expires: Date.now() + 120000 };
      void read();
    });
    if (unsubscribe) ctx.onDispose(unsubscribe);
    void read();
  }
};
