// Renders the Progress page charts from the JSON the server embedded in the page.
(function () {
  const el = document.getElementById('chartData');
  if (!el || !window.Chart) return;
  const d = JSON.parse(el.textContent);
  const css = getComputedStyle(document.documentElement);
  const c = (name) => css.getPropertyValue(name).trim();
  Chart.defaults.font.family = css.getPropertyValue('--bs-font-sans-serif');
  Chart.defaults.color = c('--ct-muted');
  const grid = { color: '#eef1f4' };

  const make = (id, config) => { const cv = document.getElementById(id); if (cv) new Chart(cv, config); };

  make('calChart', {
    data: {
      labels: d.labels,
      datasets: [
        { type: 'bar', label: 'Calories', data: d.calories, backgroundColor: c('--ct-cal'), borderRadius: 4, maxBarThickness: 28 },
        { type: 'line', label: 'Goal', data: d.calorieGoal, borderColor: c('--ct-text'), borderDash: [6, 4], pointRadius: 0, borderWidth: 1.5, stepped: true },
      ],
    },
    options: { maintainAspectRatio: false, scales: { y: { beginAtZero: true, grid }, x: { grid: { display: false } } }, plugins: { legend: { position: 'bottom' } } },
  });

  make('proteinChart', {
    type: 'line',
    data: {
      labels: d.labels,
      datasets: [
        { label: 'Protein (g)', data: d.protein, borderColor: c('--ct-protein'), backgroundColor: c('--ct-protein'), spanGaps: false, tension: 0.25 },
        { label: 'Goal', data: d.proteinGoal, borderColor: c('--ct-text'), borderDash: [6, 4], pointRadius: 0, borderWidth: 1.5, stepped: true },
      ],
    },
    options: { maintainAspectRatio: false, scales: { y: { beginAtZero: true, grid }, x: { grid: { display: false } } }, plugins: { legend: { position: 'bottom' } } },
  });

  if (d.macro) make('macroChart', {
    type: 'doughnut',
    data: { labels: ['Protein', 'Carbs', 'Fat'], datasets: [{ data: d.macro, backgroundColor: [c('--ct-protein'), c('--ct-carbs'), c('--ct-fat')], borderWidth: 0 }] },
    options: { maintainAspectRatio: false, cutout: '62%', plugins: { legend: { position: 'bottom' }, tooltip: { callbacks: { label: (ctx) => `${ctx.label}: ${ctx.parsed}% of calories` } } } },
  });

  make('weightChart', {
    type: 'line',
    data: { labels: d.weightLabels, datasets: [{ label: `Weight (${d.unit})`, data: d.weights, borderColor: c('--ct-brand'), backgroundColor: c('--ct-brand'), tension: 0.25 }] },
    options: { maintainAspectRatio: false, scales: { y: { grid }, x: { grid: { display: false } } }, plugins: { legend: { display: false } } },
  });
})();
