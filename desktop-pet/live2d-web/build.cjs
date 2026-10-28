const path = require('node:path');
const fs = require('node:fs');
const esbuild = require('esbuild');
const root = __dirname;
esbuild.buildSync({
  entryPoints: [path.join(root, 'renderer.ts')],
  outfile: path.join(root, '../assets/live2d-runtime/renderer.js'),
  bundle: true, format: 'iife', platform: 'browser', target: ['chrome110'],
  minify: true, legalComments: 'inline', charset: 'utf8',
  banner: { js: '/*! AI Pet Live2D runtime; bundles unmodified Cubism Web Framework 5-r.5 modules. Copyright (c) Live2D Inc. Framework: Live2D Open Software License. See licenses/Framework-LICENSE.md. Core is a separate, unmodified proprietary file. */' }
});
console.log('Built offline Live2D renderer.');