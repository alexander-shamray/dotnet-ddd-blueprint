// The k6 SLO run of §13.7 and §15.1 — the first real gate after the staging
// deploy.
//
// IT IS NOT A SMOKE TEST, and §15.1 declines to have one for the reason §12.1
// gives: a gate nobody has defined is a gate that gets configured to pass. This
// stage is named for what it asserts. It is also NOT a capacity test — it
// catches the regression where a query loses its index and goes from 40 ms to
// 4 s, which no unit test will find.
//
//   k6 run \
//     -e BASE_URL=https://staging.example.com \
//     -e PROM_URL=http://prometheus.observability:9090 \
//     -e TOKEN_URL=https://id.staging.example.com/realms/commerce/protocol/openid-connect/token \
//     -e CLIENT_ID=slo-run -e CLIENT_SECRET=... \
//     deploy/observability/slo/slo.js
//
// TWO INSTRUMENTS, DELIBERATELY. k6 measures wall-clock at the client, which
// includes the edge, TLS and the network; §13.7's command and query rows read
// `request.duration`, which is dispatcher entry to result. k6's number is
// therefore strictly LARGER than the thing the SLO is about, so the thresholds
// below are a coarse guard and Prometheus adjudicates in teardown(). Asserting
// only k6's would fail a healthy service on a slow link; asserting only
// Prometheus's would let a broken edge through with perfect handler timings.
import http from 'k6/http';
import { check, fail } from 'k6';
import { Trend } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL;
const PROM_URL = __ENV.PROM_URL;
const TOKEN_URL = __ENV.TOKEN_URL;
const CLIENT_ID = __ENV.CLIENT_ID;
const CLIENT_SECRET = __ENV.CLIENT_SECRET;

// Wall-clock, split by what the request IS rather than by route, so the two
// numbers line up with §13.7's two rows.
const commandDuration = new Trend('slo_command_duration', true);
const queryDuration = new Trend('slo_query_duration', true);

export const options = {
    scenarios: {
        // The anonymous product listing — §10.2's `catalog-public` route is
        // GET-only and carries no policy, so this needs no token and is the
        // cleanest read-path signal available.
        query: {
            executor: 'constant-arrival-rate',
            rate: 30,
            timeUnit: '1s',
            duration: '3m',
            preAllocatedVUs: 20,
            maxVUs: 60,
            exec: 'browseCatalog',
        },
        // Placing an order is the write path: one aggregate, the outbox row in
        // the same transaction (§6.3), and the saga started off the back of it.
        command: {
            executor: 'constant-arrival-rate',
            rate: 5,
            timeUnit: '1s',
            duration: '3m',
            preAllocatedVUs: 10,
            maxVUs: 30,
            exec: 'placeOrder',
            startTime: '10s',
        },
    },
    thresholds: {
        // Generous against §13.7's 80 ms and 100 ms, for the reason at the top
        // of this file: these include everything k6 can see and the SLO does
        // not. A breach here is a real problem; passing here is not a pass.
        slo_query_duration: ['p(95)<400'],
        slo_command_duration: ['p(95)<600'],
        // Not the availability SLO — see teardown().
        http_req_failed: ['rate<0.01'],
    },
};

export function setup() {
    // Fail fast and loudly on missing configuration. A run that quietly skips
    // the command scenario because it has no credentials reports a pass for a
    // gate that did not execute, which is the failure mode §15.1's whole
    // "no smoke stage" argument is about.
    const missing = ['BASE_URL', 'PROM_URL', 'TOKEN_URL', 'CLIENT_ID', 'CLIENT_SECRET']
        .filter((name) => !__ENV[name]);

    if (missing.length > 0)
        fail(`SLO run is not configured: missing ${missing.join(', ')}`);

    const response = http.post(TOKEN_URL, {
        grant_type: 'client_credentials',
        client_id: CLIENT_ID,
        client_secret: CLIENT_SECRET,
    });

    if (response.status !== 200)
        fail(`token endpoint returned ${response.status}: ${response.body}`);

    return { token: response.json('access_token'), startedAt: Date.now() };
}

export function browseCatalog() {
    const response = http.get(
        `${BASE_URL}/api/v1/catalog/products?limit=20`,
        { tags: { slo: 'query' } });

    queryDuration.add(response.timings.duration);
    check(response, { 'catalog listing is 200': (r) => r.status === 200 });
}

export function placeOrder(data) {
    const body = JSON.stringify({
        currency: 'GBP',
        lines: [{ productId: __ENV.SLO_PRODUCT_ID, quantity: 1 }],
    });

    const response = http.post(`${BASE_URL}/api/v1/orders`, body, {
        headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${data.token}`,
        },
        tags: { slo: 'command' },
    });

    commandDuration.add(response.timings.duration);

    // 201 is the success. 422 is the domain refusing — §10.5 maps Error.Rule
    // there — and it is checked separately rather than folded into a
    // status-class assertion, because a run whose product has no price row
    // returns 422 for every request and would otherwise look like a clean
    // load test with a suspiciously fast write path. That is §6.6's worked
    // case, and docs/runbooks/business-volume.md is the procedure for it.
    check(response, {
        'order accepted': (r) => r.status === 201,
        'order not refused by the domain': (r) => r.status !== 422,
    });
}

export function teardown(data) {
    // The authoritative half. Every row below names the instrument §13.7 says
    // it reads; a row whose signal does not exist is NOT asserted here and is
    // listed at the bottom instead, because an SLO nobody can compute is worse
    // than an absence somebody has written down.
    const window = `${Math.max(1, Math.round((Date.now() - data.startedAt) / 60000))}m`;

    const rows = [
        {
            name: 'Command p95 < 100 ms',
            // Split by naming convention: RequestMetrics tags `request` with
            // the request type's name and has no kind tag, so *Command and
            // *Query is what distinguishes the two rows. §6.2's naming is what
            // makes that reliable, and a request type named otherwise is
            // invisible to this check.
            query: `histogram_quantile(0.95, sum by (le) (rate(request_duration_seconds_bucket{request=~".+Command"}[${window}])))`,
            limit: 0.1,
        },
        {
            name: 'Query p95 < 80 ms',
            query: `histogram_quantile(0.95, sum by (le) (rate(request_duration_seconds_bucket{request=~".+Query"}[${window}])))`,
            limit: 0.08,
        },
        {
            name: 'Event end-to-end p95 < 2 s',
            query: `histogram_quantile(0.95, sum by (le) (rate(messaging_delivery_lag_seconds_bucket[${window}])))`,
            limit: 2,
        },
        {
            name: 'Outbox oldest, broker lane, p99 < 5 s',
            query: `quantile_over_time(0.99, outbox_oldest_age_seconds{lane="Broker"}[${window}])`,
            limit: 5,
        },
        {
            name: 'Outbox oldest, local lane, p99 < 1 s',
            query: `quantile_over_time(0.99, outbox_oldest_age_seconds{lane="Local"}[${window}])`,
            limit: 1,
        },
        {
            name: 'Read-model staleness, own events, p99 < 1 s',
            query: `histogram_quantile(0.99, sum by (le) (rate(projection_lag_seconds_bucket[${window}])))`,
            limit: 1,
        },
    ];

    const breaches = [];

    for (const row of rows) {
        const value = instantQuery(row.query);

        if (value === null) {
            // An absent series is a breach, not a pass. This is the whole
            // lesson of §13.6's callout: the dashboard is empty either way,
            // whether the system is healthy or the metric was never published.
            breaches.push(`${row.name}: NO DATA (query returned no series)`);
            continue;
        }

        if (value > row.limit)
            breaches.push(`${row.name}: measured ${value.toFixed(4)}, limit ${row.limit}`);
    }

    // Availability (99.9% monthly) is deliberately NOT asserted. It is a
    // monthly objective and a three-minute run cannot evaluate it; http_req_failed
    // above bounds the error rate DURING the run, which is a different and much
    // weaker claim. Saying so is the point — a run that reported "availability:
    // pass" after three minutes would be the exact defect §13.7 cut two rows to
    // avoid.
    console.log('SLO run: availability (99.9% monthly) is not evaluated by this run.');

    if (breaches.length > 0)
        fail(`SLO run failed:\n  ${breaches.join('\n  ')}`);
}

function instantQuery(query) {
    const response = http.get(`${PROM_URL}/api/v1/query?query=${encodeURIComponent(query)}`);

    if (response.status !== 200)
        fail(`Prometheus returned ${response.status} for: ${query}`);

    const result = response.json('data.result');

    if (!result || result.length === 0)
        return null;

    const value = Number(result[0].value[1]);

    return Number.isNaN(value) ? null : value;
}
