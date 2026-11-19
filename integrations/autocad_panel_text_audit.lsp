;;; Read-only top-level text, insert and attribute audit.

(defun AIENG:WriteRow (stream handle kind value)
  (write-line
    (strcat
      (if handle handle "") "\t"
      (if kind kind "") "\t"
      (if value value ""))
    stream)
)

(defun c:AIENGPANELTEXTAUDIT
  (/ output stream selection index entity data type child childData childType)
  (setq output (getenv "AI_ENGINEER_PANEL_AUDIT_OUTPUT"))
  (if (and output (/= output ""))
    (progn
      (setq stream (open output "w"))
      (if stream
        (progn
          (AIENG:WriteRow stream "HANDLE" "KIND" "VALUE")
          (setq selection (ssget "_X" '((0 . "TEXT,MTEXT,INSERT"))))
          (setq index 0)
          (if selection
            (while (< index (sslength selection))
              (setq entity (ssname selection index))
              (setq data (entget entity))
              (setq type (cdr (assoc 0 data)))
              (if (or (= type "TEXT") (= type "MTEXT"))
                (AIENG:WriteRow stream (cdr (assoc 5 data)) type (cdr (assoc 1 data))))
              (if (= type "INSERT")
                (progn
                  (AIENG:WriteRow stream (cdr (assoc 5 data)) "BLOCK" (cdr (assoc 2 data)))
                  (if (= 1 (cdr (assoc 66 data)))
                    (progn
                      (setq child (entnext entity))
                      (setq childData (if child (entget child) nil))
                      (setq childType (if childData (cdr (assoc 0 childData)) ""))
                      (while (and child (/= childType "SEQEND"))
                        (if (= childType "ATTRIB")
                          (AIENG:WriteRow
                            stream
                            (cdr (assoc 5 childData))
                            (strcat "ATTR:" (cdr (assoc 2 childData)))
                            (cdr (assoc 1 childData))))
                        (setq child (entnext child))
                        (setq childData (if child (entget child) nil))
                        (setq childType (if childData (cdr (assoc 0 childData)) "")))))))
              (setq index (1+ index))))
          (close stream)
          (princ "\nAIENG_PANEL_TEXT_AUDIT_COMPLETE")))))
  (princ)
)
