const vertexSource = `
    attribute vec2 corner;
    varying vec2 position;

    void main() {
        position = corner;
        gl_Position = vec4(corner, 0.0, 1.0);
    }
`;

const fragmentSource = `
    precision highp float;
    varying vec2 position;
    uniform float angle;

    float hash(vec3 cell) {
        return fract(sin(dot(cell, vec3(127.1, 311.7, 74.7))) * 43758.5453);
    }

    float noise(vec3 point) {
        vec3 cell = floor(point);
        vec3 f = fract(point);
        f = f * f * (3.0 - 2.0 * f);
        return mix(
            mix(mix(hash(cell), hash(cell + vec3(1, 0, 0)), f.x),
                mix(hash(cell + vec3(0, 1, 0)), hash(cell + vec3(1, 1, 0)), f.x), f.y),
            mix(mix(hash(cell + vec3(0, 0, 1)), hash(cell + vec3(1, 0, 1)), f.x),
                mix(hash(cell + vec3(0, 1, 1)), hash(cell + vec3(1, 1, 1)), f.x), f.y), f.z);
    }

    void main() {
        // Recover the visible surface normal of a unit sphere from its projected disc.
        float radius = length(position);
        vec3 normal = vec3(position, sqrt(max(0.0, 1.0 - radius * radius)));
        vec3 material = normal;
        float pitch = .29 + .10 * sin(angle);
        material.yz = mat2(cos(pitch), sin(pitch), -sin(pitch), cos(pitch)) * material.yz;
        material.xy = mat2(.87, -.49, .49, .87) * material.xy;
        float spin = angle + .6;
        material.xz = mat2(cos(spin), sin(spin), -sin(spin), cos(spin)) * material.xz;

        // The veils rotate with the material while the light stays fixed.
        float mist = noise(material * 3.1);
        float detail = noise(material * 9.0);
        float flow = 5.1 * material.y + 3.5 * material.x - 3.0 * material.z + 4.2 * mist;
        float veil = .5 + .5 * sin(flow);
        float filament = pow(veil, 13.0) * smoothstep(.21, .74, mist + .22 * detail);
        float hue = smoothstep(.15, .72, .50 - .36 * normal.x + .26 * normal.y + .20 * material.x);
        vec3 light = mix(vec3(.55, .39, .98), vec3(1.0, .34, .48), hue);
        vec3 color = mix(vec3(.041, .048, .14), vec3(.25, .14, .47), mist);
        // A brighter patch travels with the veil and disappears around the back.
        vec3 brightPatch = normalize(vec3(-.74, .31, .58));
        float patch = pow(max(0.0, dot(material, brightPatch)), 19.0);
        color += light * (.36 * pow(veil, 3.0) + filament * (.51 + .24 * detail) + .30 * patch);

        vec3 key = normalize(vec3(-.48, .66, .62));
        color *= .59 + .47 * max(0.0, dot(normal, key));
        float rim = pow(1.0 - normal.z, 3.3);
        vec3 reflected = mix(vec3(.48, .29, .93), vec3(1.0, .61, .72),
            smoothstep(-.45, .65, normal.y - .36 * normal.x));
        color += reflected * rim * (.27 + .61 * abs(dot(normal.xy, key.xy)));
        float shine = max(0.0, dot(normal, key));
        color += vec3(.87, .67, 1.0) * (.16 * pow(shine, 12.0) + .34 * pow(shine, 108.0));

        gl_FragColor = vec4(color, 1.0 - smoothstep(.992, 1.0, radius));
    }
`;

export function animateOrb(canvas, signal) {
    const gl = canvas.getContext("webgl", { alpha: true, premultipliedAlpha: false });
    if (!gl) return;

    const program = gl.createProgram();
    for (const [type, source] of [[gl.VERTEX_SHADER, vertexSource], [gl.FRAGMENT_SHADER, fragmentSource]]) {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        gl.attachShader(program, shader);
        gl.deleteShader(shader);
    }
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        gl.deleteProgram(program);
        return;
    }

    const vertices = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, vertices);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    gl.useProgram(program);
    const corner = gl.getAttribLocation(program, "corner");
    gl.enableVertexAttribArray(corner);
    gl.vertexAttribPointer(corner, 2, gl.FLOAT, false, 0, 0);
    const angle = gl.getUniformLocation(program, "angle");
    const motion = matchMedia("(prefers-reduced-motion: reduce)");
    const period = 16000;
    const start = performance.now();
    let frame;
    let lastDraw = -Infinity;

    function draw(time) {
        const turn = motion.matches ? 0 : ((time - start) % period) / period;
        gl.uniform1f(angle, turn * 2 * Math.PI);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    }

    function animate(time) {
        if (time - lastDraw >= 30) {
            draw(time);
            lastDraw = time;
        }
        frame = requestAnimationFrame(animate);
    }

    function resize() {
        const size = Math.min(420, Math.max(1, Math.round(canvas.clientWidth * devicePixelRatio)));
        canvas.width = canvas.height = size;
        gl.viewport(0, 0, size, size);
        draw(performance.now());
    }

    function updateMotion() {
        cancelAnimationFrame(frame);
        if (motion.matches) draw(performance.now());
        else frame = requestAnimationFrame(animate);
    }

    const observer = new ResizeObserver(resize);
    const stop = () => {
        cancelAnimationFrame(frame);
        observer.disconnect();
        motion.removeEventListener("change", updateMotion);
        canvas.removeEventListener("webglcontextlost", stop);
        signal.removeEventListener("abort", stop);
        gl.deleteBuffer(vertices);
        gl.deleteProgram(program);
        canvas.removeAttribute("data-rendering");
    };

    resize();
    canvas.setAttribute("data-rendering", "");
    observer.observe(canvas);
    motion.addEventListener("change", updateMotion);
    canvas.addEventListener("webglcontextlost", stop);
    signal.addEventListener("abort", stop, { once: true });
    updateMotion();
}
