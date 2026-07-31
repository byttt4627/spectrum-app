// 在加载真正的 main.js 之前，先修正 process.type
process.type = 'browser';
process._linkedBinding = process._linkedBinding || function() {};

// 存储原始 require 引用
const origRequire = module.require.bind(module);

// 重写 require 使得 require('electron') 直接返回内置模块
const Module = require('module');
const origResolve = Module._resolveFilename;

// 开始加载真正的 main.js
require('./main.js');
