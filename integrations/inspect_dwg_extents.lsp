(vl-load-com)
(defun c:AIENGDWGEXTENTS (/ ss idx obj lo hi lx ly ux uy out stream)
  (setq lx nil ly nil ux nil uy nil)
  (setq ss (ssget "_X" '((410 . "Model"))))
  (if ss
    (progn
      (setq idx 0)
      (while (< idx (sslength ss))
        (setq obj (vlax-ename->vla-object (ssname ss idx)))
        (if (not (vl-catch-all-error-p
                   (vl-catch-all-apply 'vla-GetBoundingBox (list obj 'lo 'hi))))
          (progn
            (setq lo (vlax-safearray->list lo) hi (vlax-safearray->list hi))
            (setq lx (if lx (min lx (car lo)) (car lo)))
            (setq ly (if ly (min ly (cadr lo)) (cadr lo)))
            (setq ux (if ux (max ux (car hi)) (car hi)))
            (setq uy (if uy (max uy (cadr hi)) (cadr hi)))))
        (setq idx (1+ idx)))))
  (setq out (getenv "AIENG_DWG_EXTENTS_OUT"))
  (if out
    (progn
      (setq stream (open out "w"))
      (write-line
        (strcat "INSUNITS," (itoa (getvar "INSUNITS"))
                ",COUNT," (if ss (itoa (sslength ss)) "0")
                ",MINX," (if lx (rtos lx 2 8) "")
                ",MINY," (if ly (rtos ly 2 8) "")
                ",MAXX," (if ux (rtos ux 2 8) "")
                ",MAXY," (if uy (rtos uy 2 8) "")) stream)
      (close stream)))
  (princ))
