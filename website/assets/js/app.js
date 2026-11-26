/* ==========================================================================
   GameWall 官网 · 交互
   无依赖，纯原生。所有动效都做了降级：系统开了"减少动态效果"就基本不动。
   ========================================================================== */

(function () {
  'use strict';

  var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  /* ══════════════════════════════════════════════════════════
     1. 粒子星空
     几百个缓速漂移的亮点，靠近鼠标的会被轻微推开
     用 requestAnimationFrame，页面不可见时自动停
     ══════════════════════════════════════════════════════════ */
  function initStarfield() {
    var canvas = document.getElementById('starfield');
    if (!canvas) return;

    var ctx = canvas.getContext('2d');
    var dpr = Math.min(window.devicePixelRatio || 1, 2);
    var stars = [];
    var w = 0, h = 0;
    var mouse = { x: -9999, y: -9999 };
    var raf = null;

    function resize() {
      w = window.innerWidth;
      h = window.innerHeight;
      canvas.width = w * dpr;
      canvas.height = h * dpr;
      canvas.style.width = w + 'px';
      canvas.style.height = h + 'px';
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      build();
    }

    function build() {
      // 密度跟着屏幕面积走，大屏多放点但设上限，别拖累低端机
      var count = Math.min(Math.floor((w * h) / 9000), 190);
      stars = [];
      for (var i = 0; i < count; i++) {
        stars.push({
          x: Math.random() * w,
          y: Math.random() * h,
          r: Math.random() * 1.35 + 0.35,
          // 漂移速度极慢，是"星空"不是"雪花"
          vx: (Math.random() - 0.5) * 0.055,
          vy: (Math.random() - 0.5) * 0.055,
          base: Math.random() * 0.42 + 0.12,
          phase: Math.random() * Math.PI * 2,
          // 少量星星用琥珀色，跟青色主调形成点缀
          amber: Math.random() < 0.14
        });
      }
    }

    function draw(t) {
      ctx.clearRect(0, 0, w, h);

      for (var i = 0; i < stars.length; i++) {
        var s = stars[i];

        s.x += s.vx;
        s.y += s.vy;

        // 出界就绕回来，避免星星越来越少
        if (s.x < -10) s.x = w + 10;
        if (s.x > w + 10) s.x = -10;
        if (s.y < -10) s.y = h + 10;
        if (s.y > h + 10) s.y = -10;

        // 鼠标周围的星星被轻轻推远，产生"水波"感
        var dx = s.x - mouse.x;
        var dy = s.y - mouse.y;
        var d2 = dx * dx + dy * dy;
        var push = 0;
        if (d2 < 16000) {
          push = (1 - d2 / 16000) * 16;
        }
        var px = s.x, py = s.y;
        if (push > 0.5) {
          var d = Math.sqrt(d2) || 1;
          px += (dx / d) * push;
          py += (dy / d) * push;
        }

        // 呼吸式明暗
        var alpha = s.base * (0.62 + 0.38 * Math.sin(t * 0.0011 + s.phase));

        ctx.beginPath();
        ctx.arc(px, py, s.r, 0, Math.PI * 2);
        ctx.fillStyle = s.amber
          ? 'rgba(255, 182, 77, ' + alpha + ')'
          : 'rgba(140, 210, 240, ' + alpha + ')';
        ctx.fill();
      }

      raf = requestAnimationFrame(draw);
    }

    function start() {
      if (raf === null) raf = requestAnimationFrame(draw);
    }
    function stop() {
      if (raf !== null) { cancelAnimationFrame(raf); raf = null; }
    }

    resize();
    window.addEventListener('resize', resize);

    // 页面切到后台就停，省电
    document.addEventListener('visibilitychange', function () {
      if (document.hidden) stop(); else start();
    });

    if (!reduceMotion) {
      window.addEventListener('mousemove', function (e) {
        mouse.x = e.clientX;
        mouse.y = e.clientY;
      }, { passive: true });
      window.addEventListener('mouseout', function () {
        mouse.x = mouse.y = -9999;
      });
      start();
    } else {
      // 减少动效模式下只画一帧静态星空
      draw(0);
      stop();
    }
  }

  /* ══════════════════════════════════════════════════════════
     2. 导航栏滚动状态
     ══════════════════════════════════════════════════════════ */
  function initNav() {
    var nav = document.getElementById('nav');
    if (!nav) return;

    var ticking = false;
    function onScroll() {
      if (ticking) return;
      ticking = true;
      requestAnimationFrame(function () {
        nav.classList.toggle('scrolled', window.scrollY > 24);
        ticking = false;
      });
    }
    window.addEventListener('scroll', onScroll, { passive: true });
    onScroll();

    // 移动端菜单：汉堡按钮真正展开/收起导航，
    // 菜单里的链接点了要自动收起，否则跳转后菜单还盖在内容上
    var toggle = document.getElementById('navToggle');
    var drawer = document.getElementById('navDrawer');

    if (toggle && drawer) {
      toggle.addEventListener('click', function (e) {
        e.stopPropagation();
        nav.classList.toggle('nav-open');
        toggle.setAttribute('aria-expanded',
          nav.classList.contains('nav-open') ? 'true' : 'false');
      });

      drawer.addEventListener('click', function (e) {
        if (e.target.tagName === 'A') nav.classList.remove('nav-open');
      });

      // 点空白处或按 Esc 也收起
      document.addEventListener('click', function (e) {
        if (!nav.contains(e.target)) nav.classList.remove('nav-open');
      });
      document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') nav.classList.remove('nav-open');
      });
    }
  }

  /* ══════════════════════════════════════════════════════════
     3. 滚动显现

     原本只用 IntersectionObserver，但实测发现两个问题：
       1) 无头浏览器里滚动后回调不一定触发，元素就一直透明
       2) 用 ?y= 之类方式瞬间跳转时，跳过的区块永远不会入场
     内容看不见是绝对不能接受的失败模式，所以这里改成
     "scroll 事件兜底 + IntersectionObserver 加速" 双保险：
     每次滚动都直接算一遍元素位置，确保该显示的一定会显示。
     ══════════════════════════════════════════════════════════ */
  function initReveal() {
    var els = Array.prototype.slice.call(document.querySelectorAll('.reveal'));
    if (!els.length) return;

    if (reduceMotion) {
      els.forEach(function (el) { el.classList.add('in'); });
      return;
    }

    var pending = els.slice();

    function check() {
      var vh = window.innerHeight || document.documentElement.clientHeight;
      var still = [];

      for (var i = 0; i < pending.length; i++) {
        var el = pending[i];
        var r = el.getBoundingClientRect();

        // 元素有任何一部分进入视口就显示。
        // 顶部用 -60px 的余量，让元素刚露头就开始入场，衔接更自然。
        if (r.top < vh - 60 && r.bottom > 0) {
          // 同组错开，看起来像依次浮现而不是一起蹦出来
          var siblings = el.parentElement
            ? Array.prototype.filter.call(el.parentElement.children, function (n) {
                return n.classList && n.classList.contains('reveal');
              })
            : [];
          var idx = siblings.indexOf(el);
          var delay = idx > 0 ? Math.min(idx * 70, 420) : 0;

          (function (node, d) {
            if (d === 0) { node.classList.add('in'); }
            else { setTimeout(function () { node.classList.add('in'); }, d); }
          })(el, delay);
        } else {
          still.push(el);
        }
      }

      pending = still;

      // 全部显完了就把监听摘掉
      if (!pending.length) {
        window.removeEventListener('scroll', onScroll);
        window.removeEventListener('resize', onScroll);
      }
    }

    var ticking = false;
    function onScroll() {
      if (ticking) return;
      ticking = true;
      requestAnimationFrame(function () { check(); ticking = false; });
    }

    window.addEventListener('scroll', onScroll, { passive: true });
    window.addEventListener('resize', onScroll, { passive: true });

    // 首屏立即算一次，不等用户滚动
    check();

    // 图片加载完布局会变，再算一次；宁可多算也不能漏
    window.addEventListener('load', check);
    setTimeout(check, 300);
    setTimeout(check, 1200);
  }

  /* ══════════════════════════════════════════════════════════
     4. 数字滚动
     只在数字真的进入视口时才跑，避免白算
     ══════════════════════════════════════════════════════════ */
  function initCounters() {
    var nums = document.querySelectorAll('[data-count]');
    if (!nums.length) return;

    function animate(el) {
      var target = parseInt(el.getAttribute('data-count'), 10) || 0;
      var suffix = el.getAttribute('data-suffix') || '';
      var text = el.getAttribute('data-text');

      // 特殊的"零"：不是数字，直接显示字
      if (text) {
        el.textContent = text + suffix;
        return;
      }

      if (reduceMotion) {
        el.textContent = target + suffix;
        return;
      }

      var dur = 1300;
      var start = null;
      var done = false;

      // 先无条件落到终值，再决定要不要做动画。
      // 这样即使 rAF 一次都不回调（后台标签页被暂停、节能模式、
      // 某些自动化环境），数字也是正确的，而不是永远停在 0。
      var finalText = target + suffix;
      el.textContent = finalText;

      function step(ts) {
        if (done) return;
        if (start === null) start = ts;
        var p = Math.min((ts - start) / dur, 1);
        // 缓出，末尾慢下来更有"落定"的感觉
        var eased = 1 - Math.pow(1 - p, 3);
        el.textContent = Math.round(target * eased) + suffix;
        if (p < 1) requestAnimationFrame(step);
        else { el.textContent = finalText; done = true; }
      }

      // 从一个低值起跳，保证视觉上还是"从 0 数上去"
      el.textContent = '0' + suffix;
      requestAnimationFrame(step);

      // 定时器兜底：rAF 被挂起时也能在 1.5 秒内落到终值
      setTimeout(function () {
        if (done) return;
        done = true;
        el.textContent = finalText;
      }, 1500);
    }

    if (!('IntersectionObserver' in window)) {
      nums.forEach(animate);
      return;
    }

    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        animate(entry.target);
        io.unobserve(entry.target);
      });
    }, { threshold: 0.5 });

    nums.forEach(function (el) { io.observe(el); });

    // 兜底：观察器在个别环境（如无头浏览器、被节流的后台标签页）
    // 可能不回调，数字就会一直停在初始值上。
    // 这里用位置测量补一遍，并且在一个很短的窗口后无条件收尾，
    // 保证页面里永远不会留着半截数字。
    // 注意 nums 是 NodeList，没有 slice，必须先转成数组。
    var left = Array.prototype.slice.call(nums);
    function flush() {
      var vh = window.innerHeight || document.documentElement.clientHeight;
      left = left.filter(function (el) {
        var r = el.getBoundingClientRect();
        if (r.top < vh && r.bottom > 0) { animate(el); io.unobserve(el); return false; }
        return true;
      });
    }
    window.addEventListener('scroll', flush, { passive: true });
    flush();
    setTimeout(flush, 350);

    // 最后的保险：1.5 秒后不管在哪，全部落到终值
    setTimeout(function () {
      left.forEach(function (el) { io.unobserve(el); animate(el); });
      left = [];
    }, 1500);
  }

  /* ══════════════════════════════════════════════════════════
     5. 截图标签页切换
     ══════════════════════════════════════════════════════════ */
  function initShotTabs() {
    var tabs = document.getElementById('shotTabs');
    var frame = document.getElementById('shotFrame');
    var title = document.getElementById('shotTitle');
    var caption = document.getElementById('shotCaption');
    if (!tabs || !frame) return;

    var META = {
      'dark-card':             ['卡片式视图', '2:3 竖版封面，悬停放大并浮起外发光，双击直接启动'],
      'dark-list':             ['列表式视图', '一行一条，字段对齐，扫得快；和卡片视图一键切换，选择会被记住'],
      'dark-dialog-settings':  ['设置面板', '三种主题模式、强调色跟随、更新源选择、封面联网开关都在这里'],
      'dark-dialog-toolbox':   ['工具箱 · 硬件', 'CPU / 主板 / 内存 / 显卡 / 硬盘 / 显示器 / 网卡，附「针对这台机器的建议」'],
      'dark-dialog-tidy':      ['整理桌面', '只动已收录游戏的快捷方式，全部移动到备份目录并记录原始路径'],
      'dark-dialog-log':       ['运行日志', '扫描漏了游戏、封面不对、启动失败，打开这里就能看到刚才发生了什么'],
      'light-card':            ['浅色主题', '不是简单反色：强调色切到 #1668E3，卡片阴影与描边全部重新配过']
    };

    var imgs = frame.querySelectorAll('img');

    function show(key) {
      imgs.forEach(function (img) {
        img.classList.toggle('on', img.getAttribute('data-key') === key);
      });
      var m = META[key];
      if (m) {
        if (title) title.textContent = 'GameWall — ' + m[0];
        if (caption) caption.innerHTML = '<strong>' + m[0] + '</strong> — ' + m[1];
      }
    }

    tabs.addEventListener('click', function (e) {
      var btn = e.target.closest('.shot-tab');
      if (!btn) return;
      tabs.querySelectorAll('.shot-tab').forEach(function (b) {
        b.classList.toggle('active', b === btn);
      });
      show(btn.getAttribute('data-shot'));
    });
  }

  /* ══════════════════════════════════════════════════════════
     6. FAQ 手风琴
     默认允许同时展开多个 —— 用户常要对照着看
     ══════════════════════════════════════════════════════════ */
  function initFaq() {
    var list = document.getElementById('faqList');
    if (!list) return;

    list.addEventListener('click', function (e) {
      var q = e.target.closest('.faq-q');
      if (!q) return;
      q.parentElement.classList.toggle('open');
    });
  }

  /* ══════════════════════════════════════════════════════════
     7. Hero 窗口跟随鼠标轻微转动
     幅度很小（最多 4 度），只是让画面"活"起来
     ══════════════════════════════════════════════════════════ */
  function initHeroTilt() {
    if (reduceMotion) return;
    var win = document.getElementById('heroWindow');
    if (!win) return;
    // 小屏不倾斜，否则文字会很别扭
    if (window.innerWidth < 1080) return;

    var raf = null;
    var target = { x: 0, y: 0 };
    var cur = { x: 0, y: 0 };

    window.addEventListener('mousemove', function (e) {
      target.x = (e.clientX / window.innerWidth - 0.5) * 2;
      target.y = (e.clientY / window.innerHeight - 0.5) * 2;
      if (raf === null) raf = requestAnimationFrame(tick);
    }, { passive: true });

    function tick() {
      // 插值逼近，避免跟随太硬
      cur.x += (target.x - cur.x) * 0.06;
      cur.y += (target.y - cur.y) * 0.06;

      var ry = -9 + cur.x * 4.5;
      var rx = 3.5 - cur.y * 3.5;
      win.style.transform = 'rotateY(' + ry.toFixed(2) + 'deg) rotateX(' + rx.toFixed(2) + 'deg) scale(.98)';

      // 差距还大就继续追
      if (Math.abs(target.x - cur.x) > 0.001 || Math.abs(target.y - cur.y) > 0.001) {
        raf = requestAnimationFrame(tick);
      } else {
        raf = null;
      }
    }
  }

  /* ══════════════════════════════════════════════════════════
     8. 支持 ?y=像素 直接滚到指定位置
     用途：分享页面时可以直接定位到某一段，也方便自动化截图对位。
     ══════════════════════════════════════════════════════════ */
  function initScrollParam() {
    var m = /[?&]y=(\d+)/.exec(location.search);
    if (!m) return;
    var y = parseInt(m[1], 10);

    // smooth 滚动会让定位变成动画，截图会截到中间态
    document.documentElement.style.scrollBehavior = 'auto';

    function apply() {
      document.documentElement.scrollTop = y;
      document.body.scrollTop = y;
      window.scrollTo(0, y);
    }

    // 图片没加载完时页面高度不够，滚不到位。所以等 load 之后再滚一次。
    apply();
    requestAnimationFrame(apply);
    setTimeout(apply, 120);
    setTimeout(apply, 400);
    window.addEventListener('load', apply);
  }

  /* ══════════════════════════════════════════════════════════
     启动
     ══════════════════════════════════════════════════════════ */
  function boot() {
    // 每个模块单独兜底：任何一个模块出错都不能拖垮后面的。
    // 之前这里是一串裸调用，initCounters 里一个 NodeList.slice 的
    // TypeError 就把后面的截图切换、FAQ 折叠全部带走了，页面看着正常、
    // 交互全是死的。这种"静默半死"状态比整体报错更难查。
    function safe(name, fn) {
      try { fn(); }
      catch (err) {
        if (window.console && console.error) console.error('[GameWall] ' + name + ' 初始化失败', err);
      }
    }

    // 先定位，再初始化滚动相关的观察器，否则观察器记的是旧位置
    safe('scrollParam', initScrollParam);
    safe('starfield', initStarfield);
    safe('nav', initNav);
    safe('reveal', initReveal);
    safe('counters', initCounters);
    safe('shotTabs', initShotTabs);
    safe('faq', initFaq);
    safe('heroTilt', initHeroTilt);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
