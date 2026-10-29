// Small progressive enhancements. Every page works without this file; the server validates and calculates everything.
(function () {
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const num = (v) => { const n = parseFloat(v); return Number.isFinite(n) ? n : 0; };

  // Confirm destructive actions.
  $$('form[data-confirm]').forEach((f) => f.addEventListener('submit', (e) => { if (!confirm(f.dataset.confirm)) e.preventDefault(); }));

  // Prevent double submits: disable the button once the form is actually submitting (client validation passed).
  document.addEventListener('submit', (e) => {
    const form = e.target;
    if (e.defaultPrevented) return;
    if (window.jQuery && jQuery(form).data('validator') && !jQuery(form).valid()) return;
    $$('button[type=submit][data-once]', form).forEach((b) => {
      setTimeout(() => { b.disabled = true; b.dataset.label = b.innerHTML; b.innerHTML = '<span class="spinner-border spinner-border-sm me-1" aria-hidden="true"></span>Saving…'; }, 0);
    });
  });
  // Re-enable buttons if the page is restored from the back/forward cache.
  window.addEventListener('pageshow', (e) => { if (e.persisted) $$('button[disabled][data-label]').forEach((b) => { b.disabled = false; b.innerHTML = b.dataset.label; }); });

  // ---- Profile wizard ----
  const pf = document.getElementById('profileForm');
  if (pf) {
    const tz = document.getElementById('TimeZoneId');
    try { const browserTz = Intl.DateTimeFormat().resolvedOptions().timeZone; if (tz && browserTz) tz.value = browserTz; } catch { /* keep server value */ }

    const unitSel = pf.querySelector('[name=PreferredWeightUnit]');
    const syncUnit = () => $$('[data-weight-unit]', pf).forEach((s) => { s.textContent = unitSel.value === '0' || unitSel.value === 'Kg' ? 'kg' : 'lb'; });
    unitSel?.addEventListener('change', syncUnit);

    const heightSel = document.getElementById('HeightUnit');
    const syncHeight = () => {
      const cm = heightSel.value === '0' || heightSel.value === 'Cm';
      $$('[data-height]', pf).forEach((el) => { el.hidden = (el.dataset.height === 'Cm') !== cm; });
    };
    heightSel?.addEventListener('change', syncHeight); if (heightSel) syncHeight();

    if (pf.dataset.wizard === 'true') {
      const steps = $$('[data-step]', pf);
      const labels = $$('[data-step-label]', pf);
      const back = pf.querySelector('[data-wizard-back]'), next = pf.querySelector('[data-wizard-next]'), submit = pf.querySelector('[data-wizard-submit]');
      let i = 0;
      // Server-side errors may belong to a later step: open the first step that has one.
      const errStep = steps.findIndex((s) => s.querySelector('.field-validation-error, .input-validation-error'));
      if (errStep > 0) i = errStep;
      const show = () => {
        steps.forEach((s, n) => s.classList.toggle('active', n === i));
        labels.forEach((l, n) => { l.classList.toggle('active', n === i); l.classList.toggle('done', n < i); });
        back.hidden = i === 0; next.hidden = i === steps.length - 1; submit.hidden = i !== steps.length - 1;
        steps[i].querySelector('input:not([type=hidden]), select')?.focus({ preventScroll: true });
      };
      next.addEventListener('click', () => {
        const v = window.jQuery && jQuery(pf).validate();
        const fields = $$('input, select, textarea', steps[i]).filter((f) => !f.closest('[hidden]'));
        if (v && !fields.every((f) => v.element(f) !== false)) return;
        i = Math.min(i + 1, steps.length - 1); show(); window.scrollTo({ top: 0, behavior: 'smooth' });
      });
      back.addEventListener('click', () => { i = Math.max(i - 1, 0); show(); });
      show();
    }
  }

  // ---- Nutrition goals: show only fields for the selected mode ----
  const mode = document.getElementById('modeSelect');
  if (mode) {
    const sync = () => {
      const manual = mode.value === '1' || mode.value === 'Manual';
      $$('[data-mode]').forEach((el) => { el.hidden = (el.dataset.mode === 'Manual') !== manual; });
    };
    mode.addEventListener('change', sync); sync();
  }

  // ---- Edit diary item: live rescale preview ----
  const editForm = document.getElementById('editItemForm');
  if (editForm) {
    const toggle = document.getElementById('rescaleToggle');
    const qty = editForm.querySelector('[name=Quantity]');
    const weight = editForm.querySelector('[name=WeightGrams]');
    const fields = $$('[data-nutrient]', editForm);
    const qty0 = num(qty.dataset.original), weight0 = num(weight.value);
    fields.forEach((f) => { f.dataset.base = f.value; });
    // Mirrors the server: editing weight scales by grams (servings follow); editing servings scales by servings (weight follows).
    const sync = (source) => {
      fields.forEach((f) => { f.readOnly = toggle.checked; });
      if (!toggle.checked || qty0 <= 0) return;
      let factor;
      if (source === weight && weight0 > 0 && num(weight.value) > 0) {
        factor = num(weight.value) / weight0;
        qty.step = 'any'; qty.min = '0.01'; // grams can give fractional servings like 1.33
        qty.value = +(qty0 * factor).toFixed(2);
      } else {
        factor = num(qty.value) / qty0;
        if (source === qty && weight0 > 0) weight.value = +(weight0 * factor).toFixed(1);
      }
      fields.forEach((f) => { if (f.dataset.base !== '') f.value = (num(f.dataset.base) * factor).toFixed(1); });
    };
    toggle.addEventListener('change', () => sync());
    qty.addEventListener('input', () => sync(qty));
    weight.addEventListener('input', () => sync(weight));
    sync();
  }

  // ---- Manual entry: check calories vs macros ----
  const macroCheck = document.getElementById('macroCheck');
  if (macroCheck) {
    const form = macroCheck.closest('form');
    const get = (n) => num(form.querySelector(`[data-nutrient=${n}]`)?.value);
    const update = () => {
      const kcal = get('Calories'), fromMacros = get('ProteinGrams') * 4 + get('CarbohydratesGrams') * 4 + get('FatGrams') * 9;
      macroCheck.textContent = fromMacros > 0 ? `Macros add up to about ${Math.round(fromMacros)} kcal${Math.abs(kcal - fromMacros) > Math.max(30, fromMacros * 0.2) ? '. That differs from the calories you entered, so please double-check.' : '.'}` : '';
    };
    form.addEventListener('input', update);
  }

  // ---- AI scanner: preview, downscale, loading overlay ----
  const scanForm = document.getElementById('scanForm');
  if (scanForm) {
    const input = document.getElementById('photo');
    const preview = document.getElementById('preview');
    const prompt = document.getElementById('dropPrompt');
    const zone = document.getElementById('dropzone');
    const maxBytes = 8 * 1024 * 1024, maxSide = 1600;

    ['dragenter', 'dragover'].forEach((t) => zone.addEventListener(t, () => zone.classList.add('dragover')));
    ['dragleave', 'drop'].forEach((t) => zone.addEventListener(t, () => zone.classList.remove('dragover')));

    // Resize big photos before upload: faster, cheaper, and well within provider limits.
    const downscale = (file) => new Promise((resolve) => {
      if (!/^image\/(jpeg|png|webp)$/.test(file.type)) return resolve(file);
      const img = new Image();
      img.onload = () => {
        const scale = Math.min(1, maxSide / Math.max(img.width, img.height));
        if (scale === 1 && file.size < 2 * 1024 * 1024) { URL.revokeObjectURL(img.src); return resolve(file); }
        const canvas = document.createElement('canvas');
        canvas.width = Math.round(img.width * scale); canvas.height = Math.round(img.height * scale);
        canvas.getContext('2d').drawImage(img, 0, 0, canvas.width, canvas.height);
        URL.revokeObjectURL(img.src);
        canvas.toBlob((b) => resolve(b ? new File([b], file.name.replace(/\.\w+$/, '') + '.jpg', { type: 'image/jpeg' }) : file), 'image/jpeg', 0.85);
      };
      img.onerror = () => resolve(file);
      img.src = URL.createObjectURL(file);
    });

    input.addEventListener('change', async () => {
      const file = input.files[0];
      if (!file) return;
      const small = await downscale(file);
      if (small !== file && window.DataTransfer) { const dt = new DataTransfer(); dt.items.add(small); input.files = dt.files; }
      preview.src = URL.createObjectURL(small);
      preview.classList.remove('d-none'); prompt.classList.add('d-none');
      if (small.size > maxBytes) alert('This photo is still larger than 8 MB. Please choose a smaller image.');
    });

    scanForm.addEventListener('submit', (e) => {
      if (!input.files.length) { e.preventDefault(); alert('Please choose a photo first.'); return; }
      document.getElementById('aiOverlay').classList.add('show');
    });
    window.addEventListener('pageshow', () => document.getElementById('aiOverlay')?.classList.remove('show'));
  }

  // ---- AI review: live totals, exclude rows, add rows ----
  const review = document.getElementById('reviewForm');
  if (review) {
    const body = document.getElementById('reviewRows');
    const total = () => {
      const sums = { cal: 0, p: 0, c: 0, f: 0 };
      $$('tr', body).forEach((tr) => {
        const keep = tr.querySelector('[data-include]').checked;
        tr.classList.toggle('excluded', !keep);
        if (keep) $$('[data-sum]', tr).forEach((i) => { sums[i.dataset.sum] += num(i.value); });
      });
      document.getElementById('sum-cal').textContent = Math.round(sums.cal);
      ['p', 'c', 'f'].forEach((k) => { document.getElementById('sum-' + k).textContent = sums[k].toFixed(1).replace(/\.0$/, ''); });
    };
    // Changing a row's grams scales that row's kcal and macros. The baseline is taken when the grams field
    // gets focus, so numbers the user already edited by hand are scaled from, not overwritten with AI values.
    const nutrientNames = ['Calories', 'ProteinGrams', 'CarbohydratesGrams', 'FatGrams', 'FiberGrams', 'SugarGrams'];
    const rowInputs = (tr) => nutrientNames.map((n) => tr.querySelector(`[name$=".${n}"]`)).filter(Boolean);
    body.addEventListener('focusin', (e) => {
      if (!e.target.matches('[name$=".WeightGrams"]')) return;
      const tr = e.target.closest('tr');
      tr.dataset.w0 = e.target.value;
      rowInputs(tr).forEach((i) => { i.dataset.base = i.value; });
    });
    body.addEventListener('input', (e) => {
      if (!e.target.matches('[name$=".WeightGrams"]')) return;
      const tr = e.target.closest('tr');
      const w0 = num(tr.dataset.w0), w = num(e.target.value);
      if (w0 <= 0 || w <= 0) return; // no starting weight to scale from
      rowInputs(tr).forEach((i) => { if (i.dataset.base) i.value = +(num(i.dataset.base) * w / w0).toFixed(1); });
    });
    review.addEventListener('input', total);
    review.addEventListener('change', total);
    document.getElementById('addRow').addEventListener('click', () => {
      const idx = $$('tr', body).length;
      body.insertAdjacentHTML('beforeend', document.getElementById('rowTemplate').innerHTML.replaceAll('__i__', idx));
      body.lastElementChild.querySelector('input[name$=".Name"]').focus();
    });
    total();
  }
})();
