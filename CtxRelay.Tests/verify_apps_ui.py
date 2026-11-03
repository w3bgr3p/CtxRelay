import json
from pathlib import Path
from playwright.sync_api import sync_playwright

assets = Path(__file__).resolve().parents[1] / 'CtxRelay/Sessions/Web'
output = Path(__file__).resolve().parent / 'artifacts'
output.mkdir(exist_ok=True)
state = {'busy': False, 'apps': []}
for provider in ('codex', 'claude'):
    state['apps'].append({'provider': provider, 'architecture': 'x64', 'release': None,
        'selected': None, 'pending': None, 'operation': None, 'defaultDirectory': f'C:\\Apps\\{provider}',
        'installations': [{'id': provider, 'provider': provider, 'mode': 'portable' if provider == 'codex' else 'msix',
            'path': f'C:\\Apps\\{provider}', 'version': '1.0.0', 'managed': provider == 'codex', 'updateState': 'unchecked',
            'executable': f'C:\\Apps\\{provider}\\app.exe', 'appId': 'Claude!Claude', 'canRollback': True}]})
actions = []

def route_request(route):
    request = route.request
    path = request.url.split('ctxrelay.test', 1)[1].split('?', 1)[0]
    if path == '/api/apps':
        if request.method == 'POST':
            body = request.post_data_json
            actions.append(body)
            app = next(a for a in state['apps'] if a['provider'] == body['provider'])
            if body['action'] == 'check':
                app['release'] = {'version': '2.0.0'}
                app['installations'][0]['updateState'] = 'available' if body['provider'] == 'codex' else 'current'
                app['operation'] = {'state': 'done'}
            if body['action'] == 'select':
                item = app['installations'][0]
                app['selected'] = {'path': item['appId'] if item['mode'] == 'msix' else item['executable']}
        return route.fulfill(json=state)
    if path.startswith('/api/'):
        return route.fulfill(json={'sessions': [], 'errors': [], 'sources': {}, 'index': {}})
    file = assets / ('index.html' if path == '/' else path.lstrip('/'))
    if not file.exists():
        return route.fulfill(status=404)
    if file.suffix == '.html':
        text = file.read_text(encoding='utf-8').replace('__LANG__', 'ru').replace('__LOCALE__', 'ru-RU').replace('__TOKEN__', 'test')
        return route.fulfill(body=text, content_type='text/html; charset=utf-8')
    route.fulfill(path=str(file))

with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={'width': 1440, 'height': 960})
    errors = []
    page.on('pageerror', lambda e: errors.append(str(e)))
    page.route('https://ctxrelay.test/**', route_request)
    page.goto('https://ctxrelay.test/#apps')
    page.locator('.app-panel').first.wait_for()
    assert page.locator('.app-panel').count() == 2
    assert page.locator('#library-view').is_hidden()
    assert page.locator('[data-app=claude] [data-app-action=install][data-mode=portable]').is_visible()
    assert page.locator('[data-app=claude] [data-app-action=copy]').is_enabled()
    page.locator('[data-app=codex] [data-app-action=check]').click()
    page.wait_for_function("document.querySelector('[data-app=codex]').textContent.includes('2.0.0')")
    assert page.locator('[data-app=codex] .app-health').inner_text().startswith('Есть обновление')
    assert page.locator('[data-app=codex] [data-app-action=update]').is_enabled()
    page.locator('[data-app=claude] [data-app-action=check]').click()
    page.wait_for_function("document.querySelector('[data-app=claude] .app-health').textContent.includes('обновление не требуется')")
    assert page.locator('[data-app=claude] [data-app-action=update]').is_disabled()
    state['apps'][0]['installations'][0].update(version='', updateState='unknown')
    page.evaluate('pollApps(true)')
    assert 'Не удалось определить версию' in page.locator('[data-app=codex] .app-health').inner_text()
    assert 'Не установлено' not in page.locator('[data-app=codex]').inner_text()
    state['apps'][0]['installations'][0].update(version='1.0.0', updateState='available')
    page.evaluate('pollApps(true)')
    for theme in ('graphite', 'graphite-light'):
        page.select_option('#theme', theme)
        page.wait_for_timeout(250)
        page.screenshot(path=str(output / f'apps-{theme}.png'))
        bg = page.locator('#apps-view').evaluate('(e)=>getComputedStyle(e).backgroundColor')
        assert bg == ('rgb(2, 3, 3)' if theme == 'graphite' else 'rgb(220, 223, 225)'), (theme, bg)
        assert page.locator('#apps-view').evaluate('(e)=>e.scrollWidth<=e.clientWidth')
        if theme == 'graphite-light':
            assert page.locator('body').evaluate('(e)=>getComputedStyle(e).backgroundColor') == bg
    page.locator('[data-app=claude] [data-app-action=remove]').click()
    assert page.locator('#confirm-dialog').is_visible()
    page.locator('#confirm-dialog button[value=cancel]').click()
    assert not any(a['action'] == 'remove' for a in actions)
    page.locator('#apps-toggle').click()
    assert page.locator('#apps-view').is_hidden()
    live_state=output/'apps-live-state.json'
    if live_state.exists():
        state.clear(); state.update(json.loads(live_state.read_text(encoding='utf-8')))
        page.locator('#apps-toggle').click()
        page.wait_for_function("document.querySelector('[data-app=codex]').textContent.includes('26.928.21956')")
        assert page.locator('[data-app=codex] .app-health[data-update=available]').is_visible()
        assert page.locator('[data-app=codex] [data-app-action=update]').is_enabled()
        page.select_option('#theme','graphite');page.wait_for_timeout(250)
        page.screenshot(path=str(output/'apps-live-version-status.png'))
        page.set_viewport_size({'width':1024,'height':960})
        cards=page.locator('.app-panel')
        assert cards.nth(1).bounding_box()['y'] > cards.nth(0).bounding_box()['y']
        assert all(c.bounding_box()['x']+c.bounding_box()['width']<=1024 for c in cards.all())
    assert not errors, errors
    browser.close()
print('PASS apps UI: navigation, update action, both palettes, removal cancellation, no JS errors')
